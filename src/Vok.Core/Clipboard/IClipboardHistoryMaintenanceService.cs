namespace Vok.Core.Clipboard;

public interface IClipboardHistoryMaintenanceService
{
    Task<ClipboardHistoryMaintenanceResult> ApplyRetentionAsync(
        ClipboardRetentionOptions options,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<ClipboardHistoryMaintenanceResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default);

    Task<bool> RemoveEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
}

public interface IClipboardHistoryMaintenanceRepository
{
    Task<ClipboardMetadataMaintenanceResult> ApplyRetentionMetadataAsync(
        ClipboardRetentionOptions options,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<ClipboardMetadataMaintenanceResult> ClearHistoryMetadataAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClipboardAssetDeleteClaim>> ClaimUnreferencedAssetsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> LoadKnownAssetRelativePathsAsync(
        CancellationToken cancellationToken = default);
}
