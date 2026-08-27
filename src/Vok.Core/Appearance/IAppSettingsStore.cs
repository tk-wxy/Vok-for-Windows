namespace Vok.Core.Appearance;

using Vok.Core.Platform;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveThemeModeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default);

    Task SaveHotkeyGestureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default);
}
