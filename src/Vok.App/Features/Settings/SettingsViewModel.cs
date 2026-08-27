using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Platform;
using Vok.Hotkeys;

namespace Vok.Features.Settings;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppearanceController _appearance;
    private readonly HotkeyController _hotkeys;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme))]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme))]
    private AppThemeMode _selectedThemeMode;

    [ObservableProperty] private string? _themeErrorMessage;

    [ObservableProperty] private string _hotkeyDisplayText;

    [ObservableProperty] private string? _hotkeyErrorMessage;

    [ObservableProperty] private bool _isHotkeyRecording;

    [ObservableProperty] private bool _isHotkeyBusy;

    public SettingsViewModel(
        AppearanceController appearance,
        HotkeyController hotkeys)
    {
        _appearance = appearance;
        _hotkeys = hotkeys;
        _selectedThemeMode = appearance.SelectedThemeMode;
        _hotkeyDisplayText = hotkeys.CurrentGesture.DisplayName;
        ThemeErrorMessage = appearance.InitializationError is null
            ? null
            : "无法读取已保存的主题，本次使用“跟随系统”。";
        HotkeyErrorMessage = hotkeys.InitializationError is null
            ? null
            : "无法读取已保存的快捷键，本次使用默认值。";
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

    public async Task BeginHotkeyRecordingAsync()
    {
        if (IsHotkeyRecording)
        {
            await CancelHotkeyRecordingAsync();
            return;
        }

        HotkeyErrorMessage = null;
        IsHotkeyRecording = true;
        IsHotkeyBusy = true;
        HotkeyDisplayText = "正在准备录制…";
        try
        {
            await _hotkeys.BeginCaptureAsync();
            HotkeyDisplayText = "请按下组合键";
        }
        catch (Exception exception)
        {
            IsHotkeyRecording = false;
            HotkeyDisplayText = _hotkeys.CurrentGesture.DisplayName;
            HotkeyErrorMessage = $"无法开始录制：{exception.Message}";
        }
        finally
        {
            IsHotkeyBusy = false;
        }
    }

    public void PreviewHotkeyModifiers(GlobalHotkeyModifiers modifiers)
    {
        if (!IsHotkeyRecording || IsHotkeyBusy)
        {
            return;
        }

        HotkeyDisplayText = modifiers == GlobalHotkeyModifiers.None
            ? "请按下组合键"
            : GlobalHotkeyGestureFormatter.FormatModifiers(modifiers);
    }

    public void ReportHotkeyValidationError(string message)
    {
        if (IsHotkeyRecording)
        {
            HotkeyErrorMessage = message;
        }
    }

    public async Task CompleteHotkeyRecordingAsync(GlobalHotkeyGesture gesture)
    {
        if (!IsHotkeyRecording || IsHotkeyBusy)
        {
            return;
        }

        HotkeyErrorMessage = null;
        IsHotkeyBusy = true;
        HotkeyDisplayText = $"正在应用 {gesture.DisplayName}…";
        try
        {
            await _hotkeys.CompleteCaptureAsync(gesture);
        }
        catch (GlobalHotkeyRegistrationException)
        {
            HotkeyErrorMessage = $"{gesture.DisplayName} 已被其他程序或系统占用，请选择其他组合。";
        }
        catch (Exception exception)
        {
            HotkeyErrorMessage = $"快捷键未能更新：{exception.Message}";
        }
        finally
        {
            IsHotkeyRecording = false;
            IsHotkeyBusy = false;
            HotkeyDisplayText = _hotkeys.CurrentGesture.DisplayName;
        }
    }

    public async Task CancelHotkeyRecordingAsync()
    {
        if (!IsHotkeyRecording)
        {
            return;
        }

        IsHotkeyBusy = true;
        try
        {
            await _hotkeys.CancelCaptureAsync();
        }
        catch (Exception exception)
        {
            HotkeyErrorMessage = $"原快捷键未能恢复：{exception.Message}";
        }
        finally
        {
            IsHotkeyRecording = false;
            IsHotkeyBusy = false;
            HotkeyDisplayText = _hotkeys.CurrentGesture.DisplayName;
        }
    }
}
