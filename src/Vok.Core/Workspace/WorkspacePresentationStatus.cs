namespace Vok.Core.Workspace;

public readonly record struct WorkspacePresentationStatus(
    bool IsVisible,
    bool IsTopmost,
    bool IsForeground,
    bool HasWin32KeyboardFocus,
    bool HasWpfInputFocus)
{
    public bool IsInputReady =>
        IsVisible &&
        IsTopmost &&
        IsForeground &&
        HasWin32KeyboardFocus &&
        HasWpfInputFocus;
}
