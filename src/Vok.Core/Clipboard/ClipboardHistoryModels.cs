namespace Vok.Core.Clipboard;

public enum ClipboardContentKind
{
    Text = 0,
    Image = 1,
    FileList = 2,
}

public enum ClipboardFileTransferIntent
{
    Unknown = 0,
    Copy = 1,
    Move = 2,
}

public enum ClipboardAssetRole
{
    PrimaryPng = 0,
    ThumbnailPng = 1,
}

public enum ClipboardAssetState
{
    Ready = 0,
    PendingDelete = 1,
    Quarantined = 2,
}

public abstract record ClipboardNormalizedContent(string SemanticHash);

public sealed record ClipboardStoredRepresentation(
    ClipboardRepresentationKind Kind,
    int FidelityRank,
    string InlineText,
    int ByteLength);

public sealed record ClipboardTextContent(
    string SemanticHash,
    string PlainText,
    string PlainPreview,
    IReadOnlyList<ClipboardStoredRepresentation> Representations)
    : ClipboardNormalizedContent(SemanticHash);

public sealed record ClipboardFileReference(
    int Ordinal,
    string OriginalPath,
    string CanonicalPath,
    string DisplayName);

public sealed record ClipboardFileListContent(
    string SemanticHash,
    ClipboardFileTransferIntent Intent,
    string PlainPreview,
    IReadOnlyList<ClipboardFileReference> Files)
    : ClipboardNormalizedContent(SemanticHash);

public sealed record ClipboardPreparedAsset(
    string Hash,
    string RelativePath,
    string MediaType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight);

public sealed record ClipboardAssetReference(
    string Hash,
    ClipboardAssetState State,
    long Revision);

public sealed record ClipboardStoredAsset(
    ClipboardPreparedAsset Asset,
    ClipboardAssetState State,
    long Revision);

public sealed record ClipboardImageMetadata(
    int PixelWidth,
    int PixelHeight,
    bool HasAlpha,
    int? DpiXMilli,
    int? DpiYMilli,
    ClipboardAssetReference? PrimaryPng,
    ClipboardAssetReference? ThumbnailPng);

public sealed record ClipboardImageContent(
    string SemanticHash,
    int PixelWidth,
    int PixelHeight,
    bool HasAlpha,
    int? DpiXMilli,
    int? DpiYMilli,
    ClipboardPreparedAsset PrimaryPng,
    ClipboardPreparedAsset ThumbnailPng)
    : ClipboardNormalizedContent(SemanticHash);

public sealed record ClipboardAssetDeleteClaim(
    string Hash,
    string RelativePath,
    long Revision);

public sealed record ClipboardImageUpsertResult(
    ClipboardEntry Entry,
    IReadOnlyList<ClipboardAssetDeleteClaim> PendingDeletes);

public sealed record ClipboardRemovalResult(
    bool Removed,
    IReadOnlyList<ClipboardAssetDeleteClaim> PendingDeletes);

public sealed record ClipboardEntry(
    Guid Id,
    ClipboardContentKind Kind,
    string SemanticHash,
    string PlainPreview,
    DateTimeOffset FirstCapturedUtc,
    DateTimeOffset LastCapturedUtc,
    long LastCaptureOrder,
    int CaptureCount,
    bool IsPinned,
    long Revision,
    ClipboardFileTransferIntent? FileIntent = null,
    int ItemCount = 0,
    ClipboardImageMetadata? Image = null);

public readonly record struct ClipboardPageCursor(
    long LastCaptureOrder,
    Guid Id);

public sealed record ClipboardPage(
    IReadOnlyList<ClipboardEntry> Items,
    ClipboardPageCursor? NextCursor,
    int TotalCount);
