namespace Vok.Core.Platform;

public interface ITrayRegistrationProbe
{
    TrayRegistrationProbeResult Probe(nint ownerWindow, Guid iconId);
}

public readonly record struct TrayRegistrationProbeResult(
    bool IsRegistered,
    int NativeResult);
