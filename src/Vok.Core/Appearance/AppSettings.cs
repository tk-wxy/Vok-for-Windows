using Vok.Core.Platform;

namespace Vok.Core.Appearance;

public sealed record AppSettings(
    AppThemeMode ThemeMode,
    GlobalHotkeyGesture HotkeyGesture)
{
    public AppSettings(AppThemeMode themeMode)
        : this(themeMode, GlobalHotkeyGesture.Default)
    {
    }

    public static AppSettings Default { get; } = new(
        AppThemeMode.System,
        GlobalHotkeyGesture.Default);
}
