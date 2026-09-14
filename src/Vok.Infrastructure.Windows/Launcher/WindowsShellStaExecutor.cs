using System.Collections.Concurrent;
using System.Diagnostics;
using Vok.Core.Diagnostics;

namespace Vok.Infrastructure.Windows.Launcher;

public sealed class WindowsShellStaExecutor : IDisposable
{
    private const int QueueCapacity = 128;
    private static readonly TimeSpan DefaultShutdownWatchdog =
        TimeSpan.FromSeconds(5);
    private readonly BlockingCollection<IWorkItem> _queue = new(QueueCapacity);
    private readonly Thread _thread;
    private readonly TimeSpan _shutdownWatchdog;
    private readonly RevealTrace _trace;
    private readonly long _traceSourceId;
    private int _disposed;

    public WindowsShellStaExecutor()
        : this(DefaultShutdownWatchdog)
    {
    }

    public WindowsShellStaExecutor(RevealTrace trace)
        : this(DefaultShutdownWatchdog, trace)
    {
    }

    internal WindowsShellStaExecutor(TimeSpan shutdownWatchdog, RevealTrace? trace = null)
    {
        if (shutdownWatchdog <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownWatchdog));
        }

        _shutdownWatchdog = shutdownWatchdog;
        _trace = trace ?? RevealTrace.Disabled;
        _traceSourceId = _trace.NextId();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Shell/COM STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> ExecuteAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default,
        string diagnosticKind = "Other",
        Guid diagnosticItemId = default,
        long diagnosticRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(operation, cancellationToken, _trace,
            _traceSourceId, diagnosticKind, diagnosticItemId, diagnosticRevision);
        try
        {
            item.Record("ShellEnqueueAttempt", GetDiagnosticQueueDepth());
            if (!_queue.TryAdd(item))
            {
                throw new InvalidOperationException("The Shell/COM queue is full.");
            }
            item.Record("ShellEnqueued", GetDiagnosticQueueDepth());
        }
        catch
        {
            item.Record("ShellRejected", GetDiagnosticQueueDepth());
            item.Abandon();
            throw;
        }

        return item.Task;
    }

    private int GetDiagnosticQueueDepth()
    {
        if (!_trace.Enabled) return 0;
        try { return _queue.Count; }
        catch (ObjectDisposedException) { return -1; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        if (_thread.Join(_shutdownWatchdog))
        {
            _queue.Dispose();
        }
        else
        {
            Trace.TraceWarning(
                "Shell/COM STA did not stop within its shutdown watchdog; " +
                "the background thread will be allowed to finish naturally.");
        }
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<T> _operation;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly RevealTrace _trace;
        private readonly long _traceId;
        private readonly long _sourceId;
        private readonly string _kind;
        private readonly Guid _itemId;
        private readonly long _revision;
        private int _claim;

        public WorkItem(Func<T> operation, CancellationToken cancellationToken,
            RevealTrace trace, long sourceId, string kind, Guid itemId, long revision)
        {
            _operation = operation;
            _trace = trace;
            _traceId = trace.NextId();
            _sourceId = sourceId;
            _kind = kind;
            _itemId = itemId;
            _revision = revision;
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _claim, 2, 0) == 0)
                {
                    Record("ShellCancelledBeforeStart");
                    _completion.TrySetCanceled(cancellationToken);
                }
            });
        }

        public Task<T> Task => _completion.Task;

        public void Record(string phase, int queueDepth = 0) =>
            _trace.Record(phase, sourceId: _sourceId, operationId: _traceId,
                itemId: _itemId, revision: _revision, value1: queueDepth, detail: _kind);

        public void Abandon()
        {
            _ = Interlocked.CompareExchange(ref _claim, 2, 0);
            _cancellationRegistration.Dispose();
        }

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
            {
                Record("ShellSkipped");
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            Record("ShellStarted");
            try
            {
                var result = _operation();
                Record("ShellCompleted");
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                Record("ShellFailed");
                _completion.TrySetException(exception);
            }
        }
    }
}
