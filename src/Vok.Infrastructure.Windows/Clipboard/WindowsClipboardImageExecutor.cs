using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class WindowsClipboardImageExecutor : IDisposable
{
    private static readonly TimeSpan DefaultShutdownWatchdog =
        TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<ImageWorkItem> _queue;
    private readonly ClipboardImageTemporaryStore _temporaryStore;
    private readonly IClipboardImageNormalizationEngine _normalizer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private readonly TimeSpan _shutdownWatchdog;
    private readonly int _maximumJobs;
    private Exception? _terminalFailure;
    private int _outstandingJobs;
    private int _disposed;
    private int _resourcesDisposed;
    private int _shutdownDrainCompleted;
    private int _workerManagedThreadId;

    public WindowsClipboardImageExecutor()
        : this(new ClipboardImageCaptureOptions())
    {
    }

    public WindowsClipboardImageExecutor(ClipboardImageCaptureOptions options)
        : this(
            options,
            new ClipboardImageNormalizer(options),
            ClipboardImageTemporaryStore.CreateDefault(
                static exception => Trace.TraceWarning(
                    "Clipboard image temporary cleanup failed: {0}",
                    exception)),
            DefaultShutdownWatchdog)
    {
    }

    internal WindowsClipboardImageExecutor(
        ClipboardImageCaptureOptions options,
        IClipboardImageNormalizationEngine normalizer,
        ClipboardImageTemporaryStore temporaryStore,
        TimeSpan shutdownWatchdog)
    {
        ArgumentNullException.ThrowIfNull(options);
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _temporaryStore = temporaryStore ??
            throw new ArgumentNullException(nameof(temporaryStore));
        if (shutdownWatchdog <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownWatchdog));
        }

        _maximumJobs = options.ImageWorkerCapacity;
        _shutdownWatchdog = shutdownWatchdog;
        _queue = new BlockingCollection<ImageWorkItem>(_maximumJobs);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Clipboard image worker",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    internal int OutstandingJobCount => Volatile.Read(ref _outstandingJobs);

    internal int OutstandingTemporaryLeaseCount =>
        _temporaryStore.OutstandingLeaseCount;

    internal int WorkerManagedThreadId =>
        Volatile.Read(ref _workerManagedThreadId);

    internal bool IsWorkerThreadAlive => _thread.IsAlive;

    /// <summary>
    /// Transfers ownership of <paramref name="capture"/> to the image executor.
    /// The capture is disposed on every completion, rejection, cancellation, and
    /// shutdown path. A successful result owns only its temporary PNG outputs.
    /// </summary>
    public Task<ClipboardImageNormalizationResult> NormalizeAsync(
        ClipboardCaptureResult capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var existingFailure = Volatile.Read(ref _terminalFailure);
        if (Volatile.Read(ref _disposed) != 0 || existingFailure is not null)
        {
            DisposeRejectedCapture(capture);
            return Task.FromException<ClipboardImageNormalizationResult>(
                existingFailure is null
                    ? new ObjectDisposedException(nameof(WindowsClipboardImageExecutor))
                    : new InvalidOperationException(
                        "The clipboard image worker is no longer available.",
                        existingFailure));
        }

        if (!HasExactReservation(capture))
        {
            DisposeRejectedCapture(capture);
            return Task.FromException<ClipboardImageNormalizationResult>(
                new InvalidDataException(
                    "The clipboard image job does not own its exact input and canonical byte reservation."));
        }

        if (!TryAcquireJobSlot())
        {
            DisposeRejectedCapture(capture);
            return Task.FromException<ClipboardImageNormalizationResult>(
                new InvalidOperationException(
                    "The clipboard image worker has reached its bounded capacity."));
        }

        var item = new ImageWorkItem(
            capture,
            cancellationToken,
            ReleaseJobSlot);
        if (item.IsTerminal)
        {
            return item.Task;
        }

        var terminalFailure = Volatile.Read(ref _terminalFailure);
        if (Volatile.Read(ref _disposed) != 0 || terminalFailure is not null)
        {
            item.Reject(terminalFailure is null
                ? new ObjectDisposedException(nameof(WindowsClipboardImageExecutor))
                : new InvalidOperationException(
                    "The clipboard image worker is no longer available.",
                    terminalFailure));
            return item.Task;
        }

        try
        {
            if (!_queue.TryAdd(item))
            {
                item.Reject(new InvalidOperationException(
                    "The clipboard image worker queue is full."));
            }
        }
        catch (InvalidOperationException exception)
        {
            item.Reject(Volatile.Read(ref _disposed) != 0
                ? new ObjectDisposedException(
                    nameof(WindowsClipboardImageExecutor),
                    exception)
                : new InvalidOperationException(
                    "The clipboard image worker queue is not accepting work.",
                    exception));
        }

        return item.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _shutdown.Cancel();
        while (_queue.TryTake(out var queued))
        {
            queued.CancelForShutdown();
        }

        Volatile.Write(ref _shutdownDrainCompleted, 1);

        if (!_thread.Join(_shutdownWatchdog))
        {
            Trace.TraceWarning(
                "Clipboard image worker did not stop within the {0}-second shutdown watchdog; " +
                "the background thread will release its resources when the active codec call returns.",
                _shutdownWatchdog.TotalSeconds);
            return;
        }

        DisposeResources();
    }

    private void Run()
    {
        Volatile.Write(
            ref _workerManagedThreadId,
            Environment.CurrentManagedThreadId);
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute(_normalizer, _temporaryStore, _shutdown.Token);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _terminalFailure, exception);
            try
            {
                _queue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }

            while (_queue.TryTake(out var queued))
            {
                queued.Reject(new InvalidOperationException(
                    "The clipboard image worker terminated unexpectedly.",
                    exception));
            }
        }
        finally
        {
            Volatile.Write(ref _workerManagedThreadId, 0);
            if (Volatile.Read(ref _disposed) != 0 &&
                Volatile.Read(ref _shutdownDrainCompleted) != 0)
            {
                DisposeResources();
            }
        }
    }

    private bool TryAcquireJobSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _outstandingJobs);
            if (current >= _maximumJobs)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _outstandingJobs,
                    current + 1,
                    current) == current)
            {
                return true;
            }
        }
    }

    private static bool HasExactReservation(ClipboardCaptureResult capture)
    {
        var candidates = capture.Formats.Where(static format =>
                format.CaptureState is ClipboardFormatCaptureState.Captured &&
                format.RepresentationKind is
                    ClipboardRepresentationKind.Png or
                    ClipboardRepresentationKind.DibV5 or
                    ClipboardRepresentationKind.Dib)
            .ToArray();
        if (capture.Disposition is not ClipboardCaptureDisposition.Captured ||
            candidates.Length != 1 ||
            candidates[0].ImageHeader is not { } header)
        {
            return false;
        }

        try
        {
            return candidates[0].ReservedByteLength == checked(
                candidates[0].PayloadByteLength + header.CanonicalByteLength);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void ReleaseJobSlot() =>
        Interlocked.Decrement(ref _outstandingJobs);

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _temporaryStore.Dispose();
        _queue.Dispose();
        _shutdown.Dispose();
    }

    private static void DisposeRejectedCapture(ClipboardCaptureResult capture)
    {
        try
        {
            capture.Dispose();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "A rejected clipboard image capture failed to release cleanly: {0}",
                exception);
        }
    }

    private sealed class ImageWorkItem
    {
        private readonly TaskCompletionSource<ClipboardImageNormalizationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ClipboardCaptureResult _capture;
        private readonly CancellationToken _callerCancellation;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly Action _onTerminal;
        private int _claim;
        private int _terminalPublished;

        public ImageWorkItem(
            ClipboardCaptureResult capture,
            CancellationToken callerCancellation,
            Action onTerminal)
        {
            _capture = capture;
            _callerCancellation = callerCancellation;
            _onTerminal = onTerminal;
            _cancellationRegistration = callerCancellation.Register(
                static state => ((ImageWorkItem)state!).CancelBeforeClaim(),
                this);
        }

        public Task<ClipboardImageNormalizationResult> Task => _completion.Task;

        public bool IsTerminal => Volatile.Read(ref _claim) == 2;

        public void Execute(
            IClipboardImageNormalizationEngine normalizer,
            ClipboardImageTemporaryStore temporaryStore,
            CancellationToken shutdownToken)
        {
            if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            ClipboardImageNormalizationResult? result = null;
            Exception? failure = null;
            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _callerCancellation,
                    shutdownToken);
                result = normalizer.Normalize(
                    _capture,
                    temporaryStore,
                    linkedCancellation.Token);
                linkedCancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            failure = DisposeCapture(failure);
            try
            {
                if (failure is null && result is not null)
                {
                    if (!_completion.TrySetResult(result))
                    {
                        result.Dispose();
                    }
                }
                else
                {
                    result?.Dispose();
                    PublishFailure(failure ?? new InvalidOperationException(
                        "The clipboard image normalizer returned no result."));
                }
            }
            finally
            {
                PublishTerminal();
            }
        }

        public void Reject(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            var failure = DisposeCapture(exception);
            _completion.TrySetException(failure ?? exception);
            PublishTerminal();
        }

        public void CancelForShutdown()
        {
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            var failure = DisposeCapture(failure: null);
            if (failure is null)
            {
                _completion.TrySetCanceled(new CancellationToken(canceled: true));
            }
            else
            {
                _completion.TrySetException(failure);
            }

            PublishTerminal();
        }

        private void CancelBeforeClaim()
        {
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            var failure = DisposeCapture(failure: null);
            if (failure is null)
            {
                _completion.TrySetCanceled(_callerCancellation);
            }
            else
            {
                _completion.TrySetException(failure);
            }

            PublishTerminal();
        }

        private Exception? DisposeCapture(Exception? failure)
        {
            try
            {
                _capture.Dispose();
            }
            catch (Exception cleanupException)
            {
                return failure is null
                    ? cleanupException
                    : new AggregateException(failure, cleanupException);
            }

            return failure;
        }

        private void PublishFailure(Exception failure)
        {
            if (failure is OperationCanceledException cancellation)
            {
                _completion.TrySetCanceled(cancellation.CancellationToken);
            }
            else
            {
                _completion.TrySetException(failure);
            }
        }

        private void PublishTerminal()
        {
            if (Interlocked.Exchange(ref _terminalPublished, 1) == 0)
            {
                _onTerminal();
            }
        }
    }
}

internal interface IClipboardImageNormalizationEngine
{
    ClipboardImageNormalizationResult Normalize(
        ClipboardCaptureResult capture,
        ClipboardImageTemporaryStore temporaryStore,
        CancellationToken cancellationToken);
}
