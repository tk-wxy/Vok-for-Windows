namespace Vok.Features.Launcher;

internal sealed record LauncherLayoutProfile(
    double ViewportHeight,
    int VisibleRowCount,
    double CardUnitHeight,
    double CardSurfaceHeight);
