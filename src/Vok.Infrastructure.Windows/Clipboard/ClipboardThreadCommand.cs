namespace Vok.Infrastructure.Windows.Clipboard;

internal interface IClipboardThreadCommand
{
    bool TryClaim();

    void ExecuteClaimed();

    void FailBeforeExecution(Exception exception);
}

internal sealed class ClipboardThreadCommand<T> : IClipboardThreadCommand
{
    private readonly Func<T> _operation;
    private readonly TaskCompletionSource<T> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancellationRegistration;
    private int _state;

    public ClipboardThreadCommand(Func<T> operation, CancellationToken cancellationToken)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(
                static state => ((ClipboardThreadCommand<T>)state!).CancelBeforeExecution(),
                this);
        }
    }

    public Task<T> Task => _completion.Task;

    public bool TryClaim()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
        {
            return true;
        }

        _cancellationRegistration.Dispose();
        return false;
    }

    public void ExecuteClaimed()
    {
        try
        {
            _completion.TrySetResult(_operation());
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            Volatile.Write(ref _state, 2);
            _cancellationRegistration.Dispose();
        }
    }

    public void FailBeforeExecution(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            _cancellationRegistration.Dispose();
            _completion.TrySetException(exception);
        }
    }

    private void CancelBeforeExecution()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            _ = _cancellationRegistration.Unregister();
            _completion.TrySetCanceled();
        }
    }
}
