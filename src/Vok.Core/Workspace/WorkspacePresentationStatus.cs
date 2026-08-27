namespace Vok.Core.Workspace;

public readonly record struct WorkspacePresentationStatus(
    bool IsVisible,
    bool IsTopmost,
    bool IsForeground,
    bool HasWin32KeyboardFocus,
    bool HasWpfInputFocus,
    bool IsPresented)
{
    public bool IsInputReady =>
        IsVisible &&
        IsTopmost &&
        IsForeground &&
        HasWin32KeyboardFocus &&
        HasWpfInputFocus &&
        IsPresented;
}
