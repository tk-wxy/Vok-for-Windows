namespace Vok.Core.Clipboard;

public sealed record ClipboardReplayPayload(
    ClipboardContentKind Kind,
    string SemanticHash,
    IReadOnlyList<ClipboardStoredRepresentation> Representations,
    ClipboardFileTransferIntent? FileIntent = null,
    IReadOnlyList<ClipboardFileReference>? Files = null,
    ClipboardImageMetadata? Image = null)
{
    public string? PlainText => Representations
        .FirstOrDefault(static representation =>
            representation.Kind is ClipboardRepresentationKind.UnicodeText)?
        .InlineText;

    public IReadOnlyList<ClipboardFileReference> FileReferences => Files ?? [];
}

public readonly record struct ClipboardWriteReceipt(
    uint RuntimeSequence,
    Guid OperationId,
    string SemanticHash);
