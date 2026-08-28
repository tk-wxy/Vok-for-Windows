namespace Vok.Core.Clipboard;

public enum ClipboardFileAvailability
{
    Unknown = 0,
    Available = 1,
    Missing = 2,
    Unavailable = 3,
}

public sealed record ClipboardFilePresentation(
    byte[]? IconPngBytes,
    ClipboardFileAvailability Availability);

public interface IClipboardFilePresentationService : IDisposable
{
    Task<ClipboardFilePresentation> InspectAsync(
        IReadOnlyList<ClipboardFileReference> files,
        int pixelSize,
        CancellationToken cancellationToken = default);
}
