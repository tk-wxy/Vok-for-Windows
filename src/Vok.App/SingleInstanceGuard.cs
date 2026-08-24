namespace Vok;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        HasOwnership = createdNew;
    }

    public bool HasOwnership { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (HasOwnership)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
