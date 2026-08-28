namespace Vok.Core.Clipboard;

public enum ClipboardCaptureDisposition
{
    Captured = 0,
    Empty = 1,
    Excluded = 2,
    SelfWrite = 3,
    Unsupported = 4,
    Busy = 5,
    Failed = 6,
}
public sealed record ClipboardFormatSnapshot(
    int SourceOrder,
    string FormatName,
    ClipboardRepresentationKind RepresentationKind,
    ClipboardFormatRole Role,
    ClipboardFormatCaptureState CaptureState,
    ReadOnlyMemory<byte> Payload)
{
    public int ByteLength => Payload.Length;
}

public sealed record ClipboardCaptureResult(
    uint RuntimeSequence,
    DateTimeOffset CapturedUtc,
    ClipboardCaptureDisposition Disposition,
    IReadOnlyList<ClipboardFormatSnapshot> Formats,
    string? Error = null)
{
    public bool HasCapturedContent =>
        Disposition is ClipboardCaptureDisposition.Captured &&
        Formats.Any(static format =>
            format.Role is ClipboardFormatRole.Content &&
            format.CaptureState is ClipboardFormatCaptureState.Captured or
                ClipboardFormatCaptureState.MetadataOnly);
}
