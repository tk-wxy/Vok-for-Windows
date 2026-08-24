namespace Vok.Core.Platform;

public readonly record struct WorkspacePresentationObservation(
    bool IsVisible,
    bool IsTopmost,
    bool IsForeground,
    bool HasWin32KeyboardFocus,
    nint ObservedForeground,
    nint ObservedKeyboardFocus)
{
    public bool IsOsInputReady =>
        IsVisible && IsTopmost && IsForeground && HasWin32KeyboardFocus;
}
