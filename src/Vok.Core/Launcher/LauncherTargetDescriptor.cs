namespace Vok.Core.Launcher;

public sealed record LauncherTargetDescriptor(
    LauncherItemKind Kind,
    string TargetValue,
    string CanonicalKey,
    string ResolvedName,
    string IconSourceFingerprint);

public sealed record LauncherTargetResolution(
    string Input,
    LauncherTargetDescriptor? Target,
    string? Error)
{
    public bool Succeeded => Target is not null;
}
