namespace Vok.Core.Clipboard;

public interface IClipboardCaptureNormalizer
{
    ClipboardNormalizedContent? Normalize(ClipboardCaptureResult capture);
}
