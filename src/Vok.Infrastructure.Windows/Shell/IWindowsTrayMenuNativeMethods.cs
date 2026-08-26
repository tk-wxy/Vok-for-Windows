namespace Vok.Infrastructure.Windows.Shell;

internal interface IWindowsTrayMenuNativeMethods
{
    nint CreatePopupMenu();

    void AppendMenuItem(nint menu, uint commandId, string text);

    void AppendSeparator(nint menu);

    void SetDefaultMenuItem(nint menu, uint commandId);

    bool TryGetIconAnchor(
        nint ownerWindow,
        Guid iconId,
        out TrayMenuPoint anchor);

    TrayMenuPoint GetCursorPosition();

    void SetForegroundWindow(nint ownerWindow);

    uint TrackPopupMenu(nint menu, nint ownerWindow, TrayMenuPoint anchor);

    void PostMenuDismissal(nint ownerWindow);

    void DestroyMenu(nint menu);
}

internal readonly record struct TrayMenuPoint(int X, int Y);
