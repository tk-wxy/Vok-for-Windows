using System.Windows;
using Vok.Core.Appearance;

namespace Vok.Appearance;

public sealed class WpfAppThemeApplier(Application application) : IAppThemeApplier
{
    public void Apply(AppThemeMode themeMode)
    {
        if (!application.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The WPF theme must be applied on the application UI thread.");
        }

#pragma warning disable WPF0001
        application.ThemeMode = themeMode switch
        {
            AppThemeMode.System => ThemeMode.System,
            AppThemeMode.Light => ThemeMode.Light,
            AppThemeMode.Dark => ThemeMode.Dark,
            _ => throw new ArgumentOutOfRangeException(
                nameof(themeMode),
                themeMode,
                null),
        };
#pragma warning restore WPF0001
    }
}
