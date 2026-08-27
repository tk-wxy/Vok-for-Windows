namespace Vok.Core.Platform;

public interface IConfigurableGlobalHotkeyService : IGlobalHotkeyService
{
    GlobalHotkeyGesture CurrentGesture { get; }

    Task SuspendAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task ChangeGestureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default);
}
