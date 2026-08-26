namespace Vok.Core.Platform;

public interface ITrayIconService : IDisposable
{
    event EventHandler<TrayIconCommandEventArgs>? CommandInvoked;

    event EventHandler<Exception>? Faulted;

    void Start();
}
