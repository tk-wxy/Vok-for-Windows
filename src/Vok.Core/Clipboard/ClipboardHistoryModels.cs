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
    int ItemCount = 0);

public readonly record struct ClipboardPageCursor(
    long LastCaptureOrder,
    Guid Id);

public sealed record ClipboardPage(
    IReadOnlyList<ClipboardEntry> Items,
    ClipboardPageCursor? NextCursor,
    int TotalCount);
