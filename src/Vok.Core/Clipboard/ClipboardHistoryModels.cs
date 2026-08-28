namespace Vok.Core.Clipboard;

public enum ClipboardContentKind
{
    Text = 0,
    Image = 1,
    FileList = 2,
}

public sealed record ClipboardStoredRepresentation(
    ClipboardRepresentationKind Kind,
    int FidelityRank,
    string InlineText,
    int ByteLength);

public sealed record ClipboardTextContent(
    string SemanticHash,
    string PlainText,
    string PlainPreview,
    IReadOnlyList<ClipboardStoredRepresentation> Representations);

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
    long Revision);

public readonly record struct ClipboardPageCursor(
    long LastCaptureOrder,
    Guid Id);

public sealed record ClipboardPage(
    IReadOnlyList<ClipboardEntry> Items,
    ClipboardPageCursor? NextCursor,
    int TotalCount);
