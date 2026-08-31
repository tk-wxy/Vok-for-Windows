using Vok.Core.Workspace;

namespace Vok.Core.Clipboard;

public enum ClipboardImageDestinationKind
{
    FileSystemFolder = 0,
    Desktop = 1,
}

public interface IClipboardImageDestinationClaim : IDisposable
{
    ClipboardImageDestinationKind Kind { get; }
}

public interface IClipboardImageFileCreationAuthorization
{
    bool TryClaimFileCreation();
}

public enum ClipboardImageFileCreationStatus
{
    Created = 0,
    StaleTarget = 1,
}

public readonly record struct ClipboardImageFileCreationResult(
    ClipboardImageFileCreationStatus Status,
    string? CreatedFileName,
    bool TemporaryFilesCleaned)
{
    public bool Created => Status is ClipboardImageFileCreationStatus.Created;

    public static ClipboardImageFileCreationResult StaleTarget => new(
        ClipboardImageFileCreationStatus.StaleTarget,
        CreatedFileName: null,
        TemporaryFilesCleaned: true);
}

public interface IClipboardImageDestinationService : IDisposable
{
    Task<IClipboardImageDestinationClaim?> ResolveAsync(
        ForegroundTarget target,
        CancellationToken cancellationToken = default);

    Task<ClipboardImageFileCreationResult> CreateAsync(
        IClipboardImageDestinationClaim destination,
        ClipboardImageExportMaterialization materialization,
        string requestedFileName,
        IClipboardImageFileCreationAuthorization authorization,
        CancellationToken cancellationToken = default);
}
