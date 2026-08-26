namespace Vok.Core.Appearance;

public sealed record AppSettings(AppThemeMode ThemeMode)
{
    public static AppSettings Default { get; } = new(AppThemeMode.System);
}
