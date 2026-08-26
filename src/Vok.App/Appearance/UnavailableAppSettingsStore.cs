using Vok.Core.Appearance;

namespace Vok.Appearance;

internal sealed class UnavailableAppSettingsStore(Exception failure) : IAppSettingsStore
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AppSettings>(failure);

    public Task SaveThemeModeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default) =>
        Task.FromException(failure);
}
