using System.Collections.Concurrent;
using System.Diagnostics;
using Vok.Core.Platform;

namespace Vok.Infrastructure.Windows.Input;

public sealed class GlobalHotkeyService : IConfigurableGlobalHotkeyService
{
    internal const uint CommandMessage = 0x8000 + 0x56;
    internal const uint ReleasePollIntervalMilliseconds = 10;
    private const int HotkeyId = 0x564F;
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);

    private readonly IForegroundWindowService _foregroundWindowService;
    private readonly IGlobalHotkeyNativeMethods _nativeMethods;
    private readonly TaskCompletionSource _loopReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<nuint, HotkeyCommand> _commands = [];
    private readonly Thread _messageThread;
    private readonly object _lifecycleGate = new();
    private GlobalHotkeyGesture _gesture;
    private long _nextCommandId;
    private long _nextPressGeneration;
    private int _lifecycle;
    private uint _messageThreadId;
    private nuint _releaseTimerId;
    private ActiveHotkeyPress? _activePress;
    private bool _registrationDesired = true;
    private bool _registered;

    public GlobalHotkeyService(
        IForegroundWindowService foregroundWindowService,
        GlobalHotkeyGesture gesture)
        : this(foregroundWindowService, gesture, new Win32GlobalHotkeyNativeMethods())
    {
    }

    internal GlobalHotkeyService(
        IForegroundWindowService foregroundWindowService,
        GlobalHotkeyGesture gesture,
        IGlobalHotkeyNativeMethods nativeMethods)
    {
        _foregroundWindowService = foregroundWindowService;
        _gesture = gesture;
        _nativeMethods = nativeMethods;
        _messageThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Vok.GlobalHotkey",
        };
    }

    public event EventHandler<HotkeyPressEvent>? PressChanged;

    public event EventHandler<Exception>? Faulted;

    public GlobalHotkeyGesture CurrentGesture => Volatile.Read(ref _gesture);

    public string DisplayGesture => CurrentGesture.DisplayName;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle == 2)
            {
                throw new ObjectDisposedException(nameof(GlobalHotkeyService));
            }

            if (_lifecycle == 0)
            {
                _lifecycle = 1;
                _messageThread.Start();
            }
        }

        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SuspendAsync(CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(HotkeyCommandKind.Suspend, null, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync(HotkeyCommandKind.Resume, null, cancellationToken);

    public Task ChangeGestureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return ExecuteCommandAsync(
            HotkeyCommandKind.ChangeGesture,
            gesture,
            cancellationToken);
    }

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
            threadId = _messageThreadId;
        }

        if (threadId != 0)
        {
            _ = _nativeMethods.PostQuit(threadId);
        }

        if (_messageThread.IsAlive && !_messageThread.Join(ShutdownWatchdog))
        {
            PublishFault(new TimeoutException(
                $"Global hotkey thread did not stop within the {ShutdownWatchdog.TotalSeconds:0}-second shutdown watchdog."));
        }
    }

    private Task ExecuteCommandAsync(
        HotkeyCommandKind kind,
        GlobalHotkeyGesture? gesture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (_lifecycle == 2)
            {
                throw new ObjectDisposedException(nameof(GlobalHotkeyService));
            }

            if (_lifecycle == 0)
            {
                switch (kind)
                {
                    case HotkeyCommandKind.Suspend:
                        _registrationDesired = false;
                        break;
                    case HotkeyCommandKind.Resume:
                        _registrationDesired = true;
                        break;
                    case HotkeyCommandKind.ChangeGesture:
                        Volatile.Write(ref _gesture, gesture!);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
                }

                return Task.CompletedTask;
            }
        }

        return PostCommandAsync(kind, gesture, cancellationToken);
    }

    private async Task PostCommandAsync(
        HotkeyCommandKind kind,
        GlobalHotkeyGesture? gesture,
        CancellationToken cancellationToken)
    {
        await _loopReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var commandId = checked((nuint)Interlocked.Increment(ref _nextCommandId));
        var command = new HotkeyCommand(kind, gesture);
        if (!_commands.TryAdd(commandId, command))
        {
            throw new InvalidOperationException("Unable to queue the global hotkey command.");
        }

        var threadId = Volatile.Read(ref _messageThreadId);
        if (threadId == 0 || !_nativeMethods.PostCommand(threadId, CommandMessage, commandId))
        {
            _commands.TryRemove(commandId, out _);
            throw new InvalidOperationException("The global hotkey thread is unavailable.");
        }

        // Once posted, cancellation cannot hide a registration side effect.
        await command.Task.ConfigureAwait(false);
    }

    private void RunMessageLoop()
    {
        try
        {
            var threadId = _nativeMethods.GetCurrentThreadId();
            _nativeMethods.EnsureMessageQueue();
            lock (_lifecycleGate)
            {
                _messageThreadId = threadId;
            }
            _loopReady.TrySetResult();

            if (_registrationDesired)
            {
                try
                {
                    Register(CurrentGesture);
                    _started.TrySetResult();
                }
                catch (Exception exception)
                {
                    _started.TrySetException(exception);
                    PublishFault(exception);
                }
            }
            else
            {
                _started.TrySetResult();
            }

            while (true)
            {
                var message = _nativeMethods.GetMessage();
                if (message.Result == -1)
                {
                    throw new InvalidOperationException("GetMessage failed.");
                }

                if (message.Result == 0)
                {
                    break;
                }

                if (message.Message == CommandMessage)
                {
                    ProcessCommand(message.WParam);
                }
                else if (message.Message == _nativeMethods.HotkeyMessage &&
                         message.WParam == HotkeyId &&
                         _registered)
                {
                    BeginHotkeyPress();
                }
                else if (message.Message == _nativeMethods.TimerMessage &&
                         message.WParam == _releaseTimerId &&
                         _activePress is not null)
                {
                    ObserveActivePress();
                }
            }
        }
        catch (Exception exception)
        {
            _loopReady.TrySetException(exception);
            _started.TrySetException(exception);
            PublishFault(exception);
        }
        finally
        {
            CancelActivePress();
            if (_registered)
            {
                try
                {
                    Unregister();
                }
                catch (Exception exception)
                {
                    PublishFault(exception);
                }
            }

            Volatile.Write(ref _messageThreadId, 0);
            var failure = new InvalidOperationException("The global hotkey thread has stopped.");
            foreach (var entry in _commands)
            {
                if (_commands.TryRemove(entry.Key, out var command))
                {
                    command.Fail(failure);
                }
            }
        }
    }

    private void ProcessCommand(nuint commandId)
    {
        if (!_commands.TryRemove(commandId, out var command))
        {
            return;
        }

        try
        {
            switch (command.Kind)
            {
                case HotkeyCommandKind.Suspend:
                    CancelActivePress();
                    _registrationDesired = false;
                    if (_registered)
                    {
                        Unregister();
                    }
                    break;
                case HotkeyCommandKind.Resume:
                    _registrationDesired = true;
                    if (!_registered)
                    {
                        Register(CurrentGesture);
                    }
                    break;
                case HotkeyCommandKind.ChangeGesture:
                    CancelActivePress();
                    ChangeGesture(command.Gesture!);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            command.Complete();
        }
        catch (Exception exception)
        {
            command.Fail(exception);
        }
    }

    private void ChangeGesture(GlobalHotkeyGesture candidate)
    {
        var previous = CurrentGesture;
        _registrationDesired = true;
        if (candidate == previous)
        {
            if (!_registered)
            {
                Register(previous);
            }

            return;
        }

        if (_registered)
        {
            Unregister();
        }

        try
        {
            Register(candidate);
            Volatile.Write(ref _gesture, candidate);
        }
        catch (Exception registrationFailure)
        {
            try
            {
                Register(previous);
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    "The new global hotkey failed and the previous registration could not be restored.",
                    registrationFailure,
                    restoreFailure);
            }

            throw;
        }
    }

    private void Register(GlobalHotkeyGesture gesture)
    {
        if (!_nativeMethods.RegisterHotkey(HotkeyId, gesture))
        {
            throw new GlobalHotkeyRegistrationException(
                gesture,
                _nativeMethods.GetLastError());
        }

        _registered = true;
    }

    private void Unregister()
    {
        if (!_nativeMethods.UnregisterHotkey(HotkeyId))
        {
            throw new InvalidOperationException(
                $"Unable to unregister {CurrentGesture.DisplayName} (Win32 error {_nativeMethods.GetLastError()}).");
        }

        _registered = false;
    }

    private void BeginHotkeyPress()
    {
        CancelActivePress();

        var detectedTimestamp = Stopwatch.GetTimestamp();
        var press = new ActiveHotkeyPress(
            checked(++_nextPressGeneration),
            CurrentGesture,
            detectedTimestamp);
        _activePress = press;

        try
        {
            _releaseTimerId = _nativeMethods.StartReleaseTimer(
                ReleasePollIntervalMilliseconds);
            if (_releaseTimerId == 0)
            {
                throw new InvalidOperationException(
                    "Unable to start the global hotkey release detector.");
            }
        }
        catch (Exception exception)
        {
            PublishPressChanged(new HotkeyPressStarted(
                press.Generation,
                _foregroundWindowService.Capture(),
                detectedTimestamp));
            CompleteActivePress(Stopwatch.GetTimestamp());
            PublishFault(exception);
            return;
        }

        PublishPressChanged(new HotkeyPressStarted(
            press.Generation,
            _foregroundWindowService.Capture(),
            detectedTimestamp));
        ObserveActivePress();
    }

    private void ObserveActivePress()
    {
        var press = _activePress;
        if (press is null)
        {
            return;
        }

        try
        {
            if (IsGestureDown(press.Gesture))
            {
                return;
            }

            CompleteActivePress(Stopwatch.GetTimestamp());
        }
        catch (Exception exception)
        {
            CompleteActivePress(Stopwatch.GetTimestamp());
            PublishFault(exception);
        }
    }

    private bool IsGestureDown(GlobalHotkeyGesture gesture)
    {
        if (!_nativeMethods.IsKeyDown(gesture.VirtualKey))
        {
            return false;
        }

        var modifiers = gesture.Modifiers;
        return (!modifiers.HasFlag(GlobalHotkeyModifiers.Alt) ||
                _nativeMethods.IsKeyDown(0x12)) &&
               (!modifiers.HasFlag(GlobalHotkeyModifiers.Control) ||
                _nativeMethods.IsKeyDown(0x11)) &&
               (!modifiers.HasFlag(GlobalHotkeyModifiers.Shift) ||
                _nativeMethods.IsKeyDown(0x10)) &&
               (!modifiers.HasFlag(GlobalHotkeyModifiers.Windows) ||
                _nativeMethods.IsKeyDown(0x5B) ||
                _nativeMethods.IsKeyDown(0x5C));
    }

    private void CompleteActivePress(long releasedTimestamp)
    {
        var press = _activePress;
        if (press is null)
        {
            return;
        }

        _activePress = null;
        StopReleaseTimer();
        PublishPressChanged(new HotkeyPressReleased(
            press.Generation,
            Stopwatch.GetElapsedTime(press.PressedTimestamp, releasedTimestamp),
            releasedTimestamp));
    }

    private void CancelActivePress()
    {
        var press = _activePress;
        if (press is null)
        {
            StopReleaseTimer();
            return;
        }

        _activePress = null;
        StopReleaseTimer();
        PublishPressChanged(new HotkeyPressCanceled(
            press.Generation,
            Stopwatch.GetTimestamp()));
    }

    private void StopReleaseTimer()
    {
        var timerId = _releaseTimerId;
        _releaseTimerId = 0;
        if (timerId != 0 && !_nativeMethods.StopReleaseTimer(timerId))
        {
            PublishFault(new InvalidOperationException(
                "Unable to stop the global hotkey release detector."));
        }
    }

    private void PublishPressChanged(HotkeyPressEvent pressEvent)
    {
        var handlers = PressChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<HotkeyPressEvent>>())
        {
            try
            {
                handler(this, pressEvent);
            }
            catch (Exception exception)
            {
                PublishFault(exception);
            }
        }
    }

    private void PublishFault(Exception exception)
    {
        Trace.TraceError(exception.ToString());
        var handlers = Faulted?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<Exception>>())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception subscriberException)
            {
                Trace.TraceError(subscriberException.ToString());
            }
        }
    }

    private enum HotkeyCommandKind
    {
        Suspend,
        Resume,
        ChangeGesture,
    }

    private sealed record ActiveHotkeyPress(
        long Generation,
        GlobalHotkeyGesture Gesture,
        long PressedTimestamp);

    private sealed class HotkeyCommand(
        HotkeyCommandKind kind,
        GlobalHotkeyGesture? gesture)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HotkeyCommandKind Kind { get; } = kind;

        public GlobalHotkeyGesture? Gesture { get; } = gesture;

        public Task Task => _completion.Task;

        public void Complete() => _completion.TrySetResult();

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
