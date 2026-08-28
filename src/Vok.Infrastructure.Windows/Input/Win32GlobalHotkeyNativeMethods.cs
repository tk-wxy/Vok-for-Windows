using System.Runtime.InteropServices;
using Vok.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Input;

internal sealed class Win32GlobalHotkeyNativeMethods : IGlobalHotkeyNativeMethods
{
    public uint HotkeyMessage => PInvoke.WM_HOTKEY;

    public uint TimerMessage => PInvoke.WM_TIMER;

    public uint GetCurrentThreadId() => PInvoke.GetCurrentThreadId();

    public void EnsureMessageQueue() =>
        _ = PInvoke.PeekMessage(
            out _,
            HWND.Null,
            0,
            0,
            PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

    public bool RegisterHotkey(int id, GlobalHotkeyGesture gesture) =>
        PInvoke.RegisterHotKey(
            HWND.Null,
            id,
            ToNativeModifiers(gesture.Modifiers) | HOT_KEY_MODIFIERS.MOD_NOREPEAT,
            gesture.VirtualKey);

    public bool UnregisterHotkey(int id) => PInvoke.UnregisterHotKey(HWND.Null, id);

    public nuint StartReleaseTimer(uint intervalMilliseconds) =>
        PInvoke.SetTimer(HWND.Null, 0, intervalMilliseconds, null);

    public bool StopReleaseTimer(nuint timerId) =>
        PInvoke.KillTimer(HWND.Null, timerId);

    public bool IsKeyDown(uint virtualKey) =>
        (PInvoke.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    public HotkeyThreadMessage GetMessage()
    {
        var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);
        return new HotkeyThreadMessage(
            result.Value,
            message.message,
            (nuint)message.wParam.Value);
    }

    public bool PostCommand(uint threadId, uint message, nuint commandId) =>
        PInvoke.PostThreadMessage(
            threadId,
            message,
            new WPARAM(commandId),
            default);

    public bool PostQuit(uint threadId) =>
        PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);

    public int GetLastError() => Marshal.GetLastWin32Error();

    private static HOT_KEY_MODIFIERS ToNativeModifiers(GlobalHotkeyModifiers modifiers)
    {
        var result = (HOT_KEY_MODIFIERS)0;
        if ((modifiers & GlobalHotkeyModifiers.Alt) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_ALT;
        }
        if ((modifiers & GlobalHotkeyModifiers.Control) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        }
        if ((modifiers & GlobalHotkeyModifiers.Shift) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        }
        if ((modifiers & GlobalHotkeyModifiers.Windows) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_WIN;
        }

        return result;
    }
}
