namespace Vok.Core.Clipboard;

public sealed record ClipboardReplayPayload(
    ClipboardContentKind Kind,
    string SemanticHash,
    IReadOnlyList<ClipboardStoredRepresentation> Representations)
{
    public string? PlainText => Representations
        .FirstOrDefault(static representation =>
            representation.Kind is ClipboardRepresentationKind.UnicodeText)?
        .InlineText;
}

public readonly record struct ClipboardWriteReceipt(
    uint RuntimeSequence,
    Guid OperationId,
    string SemanticHash);
