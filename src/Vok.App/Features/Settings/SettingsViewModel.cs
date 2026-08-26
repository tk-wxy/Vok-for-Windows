using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vok.Appearance;
using Vok.Core.Appearance;

namespace Vok.Features.Settings;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppearanceController _appearance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme))]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme))]
    private AppThemeMode _selectedThemeMode;

    [ObservableProperty] private string? _themeErrorMessage;

    public SettingsViewModel(AppearanceController appearance)
    {
        _appearance = appearance;
        _selectedThemeMode = appearance.SelectedThemeMode;
        ThemeErrorMessage = appearance.InitializationError is null
            ? null
            : "无法读取已保存的主题，本次使用“跟随系统”。";
    }

    public bool IsSystemTheme => SelectedThemeMode is AppThemeMode.System;

    public bool IsLightTheme => SelectedThemeMode is AppThemeMode.Light;

    public bool IsDarkTheme => SelectedThemeMode is AppThemeMode.Dark;

    [RelayCommand]
    private async Task ChangeTheme(AppThemeMode themeMode)
    {
        if (themeMode == SelectedThemeMode)
        {
            return;
        }

        ThemeErrorMessage = null;
        SelectedThemeMode = themeMode;
        try
        {
            await _appearance.ChangeThemeAsync(themeMode);
        }
        catch (Exception exception)
        {
            SelectedThemeMode = _appearance.SelectedThemeMode;
            ThemeErrorMessage = $"主题未能保存：{exception.Message}";
        }
    }
}
