using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class WindowsClipboardPlatformService : IClipboardPlatformService
{
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);

    private readonly ClipboardCaptureOptions _options;
    private readonly IWindowsClipboardNativeApi _native;
    private readonly Guid _instanceId;
    private readonly ClipboardSequenceCoordinator _sequences = new();
    private readonly ClipboardDiagnosticState _diagnostics = new();
    private readonly Channel<ClipboardCaptureResult> _snapshots;
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly object _lifecycleGate = new();
    private readonly object _retryGate = new();
    private readonly ClipboardListenerNativeMethods.WindowProcedure _windowProcedure;
    private readonly ConcurrentQueue<IClipboardThreadCommand> _commands = new();
    private WindowsClipboardSnapshotReader? _reader;
    private WindowsClipboardWriter? _writer;
    private Timer? _retryTimer;
    private nint _listenerWindow;
    private uint _threadId;
    private int _busyRetryIndex;
    private int _lifecycle;
    private bool _retryScheduled;

    public WindowsClipboardPlatformService()
        : this(new ClipboardCaptureOptions(), new Win32ClipboardNativeApi(), Guid.NewGuid())
    {
    }

    public WindowsClipboardPlatformService(ClipboardCaptureOptions options)
        : this(options, new Win32ClipboardNativeApi(), Guid.NewGuid())
    {
    }

    internal WindowsClipboardPlatformService(
        ClipboardCaptureOptions options,
        IWindowsClipboardNativeApi native,
        Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(native);
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        }

        _options = options;
        _native = native;
        _instanceId = instanceId;
        _snapshots = Channel.CreateBounded<ClipboardCaptureResult>(
            new BoundedChannelOptions(options.SnapshotQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        _windowProcedure = WindowProcedure;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Clipboard STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    internal Guid InstanceId => _instanceId;

    internal nint ListenerWindowHandle => Volatile.Read(ref _listenerWindow);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle == 2)
            {
                throw new ObjectDisposedException(nameof(WindowsClipboardPlatformService));
            }

            if (_lifecycle == 0)
            {
                _lifecycle = 1;
                _thread.Start();
            }
        }

        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ClipboardCaptureResult> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var snapshot in _snapshots.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    public async Task<ClipboardWriteReceipt> WriteAsync(
        ClipboardReplayPayload payload,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await EnqueueCommand(
                () => _writer!.Write(_listenerWindow, payload, operationId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsCurrentAsync(
        ClipboardWriteReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        if (receipt.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.SemanticHash))
        {
            return false;
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await EnqueueCommand(
                () => _writer!.IsCurrent(_listenerWindow, receipt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ClipboardDiagnosticSnapshot GetDiagnosticSnapshot() =>
        _diagnostics.Snapshot();

    public void Dispose()
    {
        uint threadId;
        lock (_lifecycleGate)
        {
            if (_lifecycle == 2)
            {
                return;
            }

            _lifecycle = 2;
            threadId = _threadId;
            if (threadId == 0 && !_thread.IsAlive)
            {
                _started.TrySetException(
                    new ObjectDisposedException(nameof(WindowsClipboardPlatformService)));
                _snapshots.Writer.TryComplete();
                return;
            }
        }

        CancelRetry();
        if (threadId != 0)
        {
            _ = ClipboardListenerNativeMethods.PostQuit(threadId);
        }

        if (_thread.IsAlive && !_thread.Join(ShutdownWatchdog))
        {
            _diagnostics.Fault(new TimeoutException(
                $"Clipboard STA did not stop within the {ShutdownWatchdog.TotalSeconds:0}-second shutdown watchdog."));
        }
    }

    private void Run()
    {
        WindowClassRegistration registration = default;
        var classRegistered = false;
        var listenerRegistered = false;
        Exception? terminalFailure = null;
        try
        {
            _threadId = ClipboardListenerNativeMethods.GetCurrentNativeThreadId();
            _diagnostics.Started(
                Thread.CurrentThread.GetApartmentState() is ApartmentState.STA);
            lock (_lifecycleGate)
            {
                if (_lifecycle == 2)
                {
                    return;
                }
            }

            _reader = new WindowsClipboardSnapshotReader(
                _native,
                _options,
                _instanceId,
                (sequence, marker) => _writer is not null &&
                    _writer.IsExpectedSelfWrite(sequence, marker));
            _writer = new WindowsClipboardWriter(
                _native,
                _reader.KnownFormats,
                _instanceId);
            var className = $"Vok.Clipboard.Listener.{Guid.NewGuid():N}";
            registration = ClipboardListenerNativeMethods.RegisterWindowClass(
                className,
                _windowProcedure);
            classRegistered = true;
            _listenerWindow = ClipboardListenerNativeMethods.CreateMessageOnlyWindow(
                registration);
            var initialSequence = _native.GetSequenceNumber();
            _sequences.Initialize(initialSequence);
            listenerRegistered = ClipboardListenerNativeMethods.AddListener(
                _listenerWindow);
            if (!listenerRegistered)
            {
                throw new InvalidOperationException(
                    $"AddClipboardFormatListener failed with Win32 error {ClipboardListenerNativeMethods.GetLastError()}.");
            }

            _diagnostics.ListenerRegistered();
            var sequenceAfterRegistration = _native.GetSequenceNumber();
            if (sequenceAfterRegistration != initialSequence)
            {
                var notification = _sequences.Notify(sequenceAfterRegistration);
                _diagnostics.Signal(
                    sequenceAfterRegistration,
                    notification,
                    _sequences.GetState());
                if (notification.ShouldScheduleCapture)
                {
                    PostCaptureMessageOrFail(sequenceAfterRegistration);
                }
            }

            _started.TrySetResult();

            while (true)
            {
                var message = ClipboardListenerNativeMethods.GetNextMessage();
                if (message.Result == -1)
                {
                    throw new InvalidOperationException(
                        $"GetMessage failed with Win32 error {ClipboardListenerNativeMethods.GetLastError()}.");
                }

                if (message.Result == 0)
                {
                    break;
                }

                var nativeMessage = message.Message;
                ClipboardListenerNativeMethods.Dispatch(in nativeMessage);
            }
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            _started.TrySetException(exception);
            _diagnostics.Fault(exception);
            Trace.TraceError(exception.ToString());
        }
        finally
        {
            CancelRetry();
            if (listenerRegistered && _listenerWindow != 0)
            {
                _ = ClipboardListenerNativeMethods.RemoveListener(_listenerWindow);
            }

            if (_listenerWindow != 0)
            {
                _ = ClipboardListenerNativeMethods.Destroy(_listenerWindow);
                _listenerWindow = 0;
            }

            if (classRegistered)
            {
                _ = ClipboardListenerNativeMethods.Unregister(registration);
            }

            _threadId = 0;
            _diagnostics.Stopped();
            FailPendingCommands(terminalFailure ?? new ObjectDisposedException(
                nameof(WindowsClipboardPlatformService)));
            _snapshots.Writer.TryComplete(terminalFailure);
        }
    }

    private nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam)
    {
        try
        {
            if (message == ClipboardListenerNativeMethods.ClipboardUpdateMessage)
            {
                OnClipboardUpdate();
                return 0;
            }

            if (message == ClipboardListenerNativeMethods.CaptureMessage)
            {
                CaptureActiveSnapshot();
                return 0;
            }

            if (message == ClipboardListenerNativeMethods.CommandMessage)
            {
                ProcessCommands();
                return 0;
            }
        }
        catch (Exception exception)
        {
            _diagnostics.Fault(exception);
            Trace.TraceError(exception.ToString());
            RecoverActiveCapture(exception);
        }

        return ClipboardListenerNativeMethods.DefaultWindowProcedure(
            window,
            message,
            wParam,
            lParam);
    }

    private void RecoverActiveCapture(Exception exception)
    {
        try
        {
            var state = _sequences.GetState();
            if (!state.HasActive)
            {
                return;
            }

            CompleteAndPublish(new ClipboardCaptureResult(
                state.ActiveSequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Failed,
                [],
                $"Clipboard message processing failed: {exception.Message}"));
        }
        catch (Exception recoveryFailure)
        {
            var aggregate = new AggregateException(
                "Clipboard capture failure recovery also failed.",
                exception,
                recoveryFailure);
            _diagnostics.Fault(aggregate);
            Trace.TraceError(aggregate.ToString());
            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
            {
                _ = ClipboardListenerNativeMethods.PostQuit(threadId);
            }
        }
    }

    private void OnClipboardUpdate()
    {
        var sequence = _native.GetSequenceNumber();
        var notification = _sequences.Notify(sequence);
        var state = _sequences.GetState();
        _diagnostics.Signal(sequence, notification, state);
        if (notification.ShouldScheduleCapture)
        {
            PostCaptureMessageOrFail(sequence);
            return;
        }

        if (!notification.IsDuplicate && CancelRetry())
        {
            _busyRetryIndex = 0;
            PostCaptureMessageOrFail(sequence);
        }
    }

    private void CaptureActiveSnapshot()
    {
        var state = _sequences.GetState();
        if (!state.HasActive)
        {
            return;
        }

        _diagnostics.CaptureAttempt();
        var result = _reader!.Read(_listenerWindow);
        if (result.Disposition is ClipboardCaptureDisposition.Busy &&
            _busyRetryIndex < _options.BusyRetrySchedule.Count)
        {
            _diagnostics.BusyRetry();
            ScheduleRetry(_options.BusyRetrySchedule[_busyRetryIndex++]);
            return;
        }

        _busyRetryIndex = 0;
        CompleteAndPublish(result);
    }

    private void CompleteAndPublish(ClipboardCaptureResult result)
    {
        var completion = _sequences.Complete(result.RuntimeSequence);
        var state = _sequences.GetState();
        _diagnostics.Completed(result, state);
        if (!_snapshots.Writer.TryWrite(result))
        {
            _diagnostics.DroppedSnapshot();
        }

        if (completion.ShouldScheduleCapture)
        {
            PostCaptureMessageOrFail(completion.Sequence);
        }
    }

    private void ScheduleRetry(TimeSpan delay)
    {
        lock (_retryGate)
        {
            _retryTimer?.Dispose();
            _retryScheduled = true;
            _retryTimer = new Timer(
                static state => ((WindowsClipboardPlatformService)state!).PostRetry(),
                this,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void PostRetry()
    {
        lock (_retryGate)
        {
            _retryScheduled = false;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        var window = Volatile.Read(ref _listenerWindow);
        if (window != 0 && !ClipboardListenerNativeMethods.PostWindowMessage(
                window,
                ClipboardListenerNativeMethods.CaptureMessage))
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle != 2)
                {
                    _diagnostics.Fault(new InvalidOperationException(
                        $"Unable to post clipboard retry (Win32 error {ClipboardListenerNativeMethods.GetLastError()})."));
                }
            }
        }
    }

    private bool CancelRetry()
    {
        lock (_retryGate)
        {
            var wasScheduled = _retryScheduled;
            _retryScheduled = false;
            _retryTimer?.Dispose();
            _retryTimer = null;
            return wasScheduled;
        }
    }

    private void PostCaptureMessageOrFail(uint sequence)
    {
        if (ClipboardListenerNativeMethods.PostWindowMessage(
                _listenerWindow,
                ClipboardListenerNativeMethods.CaptureMessage))
        {
            return;
        }

        CompleteAndPublish(new ClipboardCaptureResult(
            sequence,
            DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Failed,
            [],
            $"Unable to post clipboard capture command (Win32 error {ClipboardListenerNativeMethods.GetLastError()})."));
    }

    private Task<T> EnqueueCommand<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _lifecycle) == 2,
            this);
        var command = new ClipboardThreadCommand<T>(operation, cancellationToken);
        _commands.Enqueue(command);
        var window = Volatile.Read(ref _listenerWindow);
        if (window == 0 || !ClipboardListenerNativeMethods.PostWindowMessage(
                window,
                ClipboardListenerNativeMethods.CommandMessage))
        {
            command.FailBeforeExecution(new InvalidOperationException(
                $"Unable to post clipboard command (Win32 error {ClipboardListenerNativeMethods.GetLastError()})."));
        }

        return command.Task;
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            if (!command.TryClaim())
            {
                continue;
            }

            command.ExecuteClaimed();
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.TryDequeue(out var command))
        {
            command.FailBeforeExecution(exception);
        }
    }
}
