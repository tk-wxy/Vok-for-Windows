namespace Vok.Core.Clipboard;

public interface IClipboardImageCaptureService
{
    /// <summary>
    /// Transfers ownership of <paramref name="capture"/> to the image pipeline.
    /// Every success, rejection, cancellation, and failure path must release it.
    /// </summary>
    Task<ClipboardEntry> CaptureAsync(
        ClipboardCaptureResult capture,
        CancellationToken cancellationToken = default);
}
