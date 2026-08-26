namespace Vok.Core.Appearance;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveThemeModeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default);
}
