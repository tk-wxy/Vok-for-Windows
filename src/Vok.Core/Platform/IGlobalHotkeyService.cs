namespace Vok.Core.Platform;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<HotkeyInvocation>? Pressed;

    event EventHandler<Exception>? Faulted;

    string DisplayGesture { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
