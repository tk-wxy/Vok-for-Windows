namespace Vok.Core.Platform;

public enum HotkeyReleaseAction
{
    KeepVisible,
    Hide,
}

public static class HotkeyPressSemantics
{
    public const int TapMaxDurationMilliseconds = 250;

    public static readonly TimeSpan TapMaxDuration =
        TimeSpan.FromMilliseconds(TapMaxDurationMilliseconds);

    public static HotkeyReleaseAction GetReleaseAction(
        TimeSpan heldDuration,
        bool workspaceWasOpenAtPress)
    {
        if (heldDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heldDuration));
        }

        return heldDuration > TapMaxDuration || workspaceWasOpenAtPress
            ? HotkeyReleaseAction.Hide
            : HotkeyReleaseAction.KeepVisible;
    }
}
