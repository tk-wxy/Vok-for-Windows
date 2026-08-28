using System.Collections.Concurrent;
using System.Diagnostics;

namespace Vok.Infrastructure.Windows.Launcher;

public sealed class WindowsShellStaExecutor : IDisposable
{
    private const int QueueCapacity = 128;
    private static readonly TimeSpan DefaultShutdownWatchdog =
        TimeSpan.FromSeconds(5);
    private readonly BlockingCollection<IWorkItem> _queue = new(QueueCapacity);
    private readonly Thread _thread;
    private readonly TimeSpan _shutdownWatchdog;
    private int _disposed;

    public WindowsShellStaExecutor()
        : this(DefaultShutdownWatchdog)
    {
    }

    internal WindowsShellStaExecutor(TimeSpan shutdownWatchdog)
    {
        if (shutdownWatchdog <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownWatchdog));
        }

        _shutdownWatchdog = shutdownWatchdog;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(operation, cancellationToken);
        try
        {
            if (!_queue.TryAdd(item))
            {
                throw new InvalidOperationException("The Shell/COM queue is full.");
            }
        }
        catch
        {
            item.Abandon();
            throw;
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
        private int _claim;

        public WorkItem(Func<T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _claim, 2, 0) == 0)
                {
                    _completion.TrySetCanceled(cancellationToken);
                }
            });
        }

        public Task<T> Task => _completion.Task;

        public void Abandon()
        {
            _ = Interlocked.CompareExchange(ref _claim, 2, 0);
            _cancellationRegistration.Dispose();
        }

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            try
            {
                _completion.TrySetResult(_operation());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
