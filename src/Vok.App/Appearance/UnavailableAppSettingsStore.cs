using Vok.Core.Appearance;
using Vok.Core.Platform;

namespace Vok.Appearance;

internal sealed class UnavailableAppSettingsStore(Exception failure) : IAppSettingsStore
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AppSettings>(failure);

    public Task SaveThemeModeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default) =>
        Task.FromException(failure);

    public Task SaveHotkeyGestureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default) =>
        Task.FromException(failure);
}
