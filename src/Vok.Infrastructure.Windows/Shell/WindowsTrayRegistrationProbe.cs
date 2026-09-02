using Vok.Core.Platform;

namespace Vok.Infrastructure.Windows.Shell;

public sealed class WindowsTrayRegistrationProbe : ITrayRegistrationProbe
{
    public TrayRegistrationProbeResult Probe(nint ownerWindow, Guid iconId)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException(
                "The tray owner window is unavailable.",
                nameof(ownerWindow));
        }

        if (iconId == Guid.Empty)
        {
            throw new ArgumentException(
                "The tray icon identifier is unavailable.",
                nameof(iconId));
        }

        var nativeResult = WindowsTrayRegistrationNativeMethods.GetIconRectangle(
            ownerWindow,
            iconId,
            out _);
        return new TrayRegistrationProbeResult(
            IsRegistered: nativeResult >= 0,
            NativeResult: nativeResult);
    }
}
