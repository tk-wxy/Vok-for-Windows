using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Shell;

internal sealed unsafe partial class WindowsTrayMenuNativeMethods
    : IWindowsTrayMenuNativeMethods
{
    public nint CreatePopupMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu == default)
        {
            throw CreateLastErrorException("Unable to create the tray menu.");
        }

        return menu;
    }

    public void AppendMenuItem(nint menu, uint commandId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        fixed (char* textPointer = text)
        {
            if (!PInvoke.AppendMenu(
                    (HMENU)menu,
                    MENU_ITEM_FLAGS.MF_STRING,
                    commandId,
                    new PCWSTR(textPointer)))
            {
                throw CreateLastErrorException("Unable to append a tray menu item.");
            }
        }
    }

    public void AppendSeparator(nint menu)
    {
        if (!PInvoke.AppendMenu(
                (HMENU)menu,
                MENU_ITEM_FLAGS.MF_SEPARATOR,
                0,
                default))
        {
            throw CreateLastErrorException("Unable to append a tray menu separator.");
        }
    }

    public void SetDefaultMenuItem(nint menu, uint commandId)
    {
        if (!PInvoke.SetMenuDefaultItem((HMENU)menu, commandId, 0))
        {
            throw CreateLastErrorException("Unable to set the default tray menu item.");
        }
    }

    public bool TryGetIconAnchor(
        nint ownerWindow,
        Guid iconId,
        out TrayMenuPoint anchor)
    {
        if (WindowsTrayRegistrationNativeMethods.GetIconRectangle(
                ownerWindow,
                iconId,
                out var rectangle) < 0)
        {
            anchor = default;
            return false;
        }

        anchor = new TrayMenuPoint(
            rectangle.Left + ((rectangle.Right - rectangle.Left) / 2),
            rectangle.Top + ((rectangle.Bottom - rectangle.Top) / 2));
        return true;
    }

    public TrayMenuPoint GetCursorPosition()
    {
        if (!PInvoke.GetCursorPos(out var point))
        {
            throw CreateLastErrorException("Unable to locate the tray menu anchor.");
        }

        return new TrayMenuPoint(point.X, point.Y);
    }

    public void SetForegroundWindow(nint ownerWindow) =>
        _ = PInvoke.SetForegroundWindow(new HWND((void*)ownerWindow));

    public uint TrackPopupMenu(nint menu, nint ownerWindow, TrayMenuPoint anchor)
    {
        var result = PInvoke.TrackPopupMenuEx(
            (HMENU)menu,
            (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON |
                TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY |
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD),
            anchor.X,
            anchor.Y,
            new HWND((void*)ownerWindow),
            null);
        return unchecked((uint)(int)result);
    }

    public void PostMenuDismissal(nint ownerWindow)
    {
        if (!PInvoke.PostMessage(
                new HWND((void*)ownerWindow),
                PInvoke.WM_NULL,
                default,
                default))
        {
            throw CreateLastErrorException("Unable to finalize tray menu dismissal.");
        }
    }

    public void DestroyMenu(nint menu)
    {
        if (!PInvoke.DestroyMenu((HMENU)menu))
        {
            throw CreateLastErrorException("Unable to destroy the tray menu.");
        }
    }

    private static Win32Exception CreateLastErrorException(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

}
