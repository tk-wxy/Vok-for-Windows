namespace Vok.Core.Clipboard;

public interface IClipboardImageMetadataRepository
{
    Task<ClipboardImageUpsertResult> UpsertImageAsync(
        ClipboardImageContent content,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default);

    Task<ClipboardRemovalResult> RemoveWithAssetClaimsAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClipboardAssetDeleteClaim>> LoadPendingAssetDeletesAsync(
        CancellationToken cancellationToken = default);

    Task<ClipboardStoredAsset?> LoadAssetAsync(
        string assetHash,
        CancellationToken cancellationToken = default);

    Task<bool> IsAssetDeleteClaimCurrentAsync(
        ClipboardAssetDeleteClaim claim,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAssetDeleteAsync(
        ClipboardAssetDeleteClaim claim,
        CancellationToken cancellationToken = default);

    Task<ClipboardAssetReference?> MarkAssetQuarantinedAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
