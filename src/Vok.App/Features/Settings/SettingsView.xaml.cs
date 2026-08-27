using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vok.Core.Platform;
using Vok.Presentation;

namespace Vok.Features.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Settings =>
        (DataContext as MainWindowViewModel)?.Settings;

    private async void OnHotkeyRecorderClick(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        await settings.BeginHotkeyRecordingAsync();
        if (settings.IsHotkeyRecording)
        {
            _ = Keyboard.Focus(HotkeyRecorderButton);
        }
    }

    private async void OnHotkeyRecorderPreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (Settings is not { IsHotkeyRecording: true } settings)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            await settings.CancelHotkeyRecordingAsync();
            return;
        }

        if (settings.IsHotkeyBusy)
        {
            return;
        }

        var modifiers = ToGlobalModifiers(Keyboard.Modifiers);
        if (IsModifierKey(key))
        {
            settings.PreviewHotkeyModifiers(modifiers);
            return;
        }

        if (modifiers == GlobalHotkeyModifiers.None)
        {
            settings.ReportHotkeyValidationError("请至少同时按下 Ctrl、Alt、Shift 或 Win 中的一个修饰键。");
            return;
        }

        var virtualKeyValue = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKeyValue <= 0 ||
            !GlobalHotkeyGesture.TryCreate(
                modifiers,
                (uint)virtualKeyValue,
                out var gesture))
        {
            settings.ReportHotkeyValidationError("这个按键组合不能注册为全局快捷键，请选择其他组合。");
            return;
        }

        await settings.CompleteHotkeyRecordingAsync(gesture!);
    }

    private async void OnHotkeyRecorderLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (Settings is { IsHotkeyRecording: true } settings)
        {
            await settings.CancelHotkeyRecordingAsync();
        }
    }

    private async void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false &&
            Settings is { IsHotkeyRecording: true } settings)
        {
            await settings.CancelHotkeyRecordingAsync();
        }
    }

    private static GlobalHotkeyModifiers ToGlobalModifiers(ModifierKeys modifiers)
    {
        var result = GlobalHotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= GlobalHotkeyModifiers.Control;
        }
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            result |= GlobalHotkeyModifiers.Alt;
        }
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= GlobalHotkeyModifiers.Shift;
        }
        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            result |= GlobalHotkeyModifiers.Windows;
        }

        return result;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftAlt or Key.RightAlt or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;
}
