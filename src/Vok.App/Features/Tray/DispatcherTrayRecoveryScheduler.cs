using System.Windows.Threading;

namespace Vok.Features.Tray;

internal sealed class DispatcherTrayRecoveryScheduler : ITrayRecoveryScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Action? _callback;
    private bool _disposed;

    public DispatcherTrayRecoveryScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnTick;
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        ArgumentNullException.ThrowIfNull(callback);
        _timer.Stop();
        _callback = callback;
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.VerifyAccess();
        _timer.Stop();
        _callback = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.VerifyAccess();
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _callback = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
    }
}
