namespace Vok.Core.Clipboard;

public interface IClipboardCaptureNormalizer
{
    ClipboardTextContent? NormalizeText(ClipboardCaptureResult capture);
}
