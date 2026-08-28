namespace Vok.Core.Platform;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<HotkeyPressEvent>? PressChanged;

    event EventHandler<Exception>? Faulted;

    string DisplayGesture { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
