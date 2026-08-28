namespace Vok.Core.Launcher;

public interface ILauncherShellService : IDisposable
{
    Task<IReadOnlyList<LauncherTargetResolution>> ResolveAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);

    Task<LauncherIconData?> GetIconAsync(
        LauncherItem item,
        int pixelSize,
        CancellationToken cancellationToken = default);

    Task OpenAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default);

    Task RevealInExplorerAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default);
}

public sealed record LauncherIconData(
    byte[] PngBytes,
    string SourceFingerprint,
    int PixelSize);
