using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ClipboardContentNormalizer : IClipboardCaptureNormalizer
{
    private readonly ClipboardTextNormalizer _text;
    private readonly ClipboardFileListNormalizer _files;

    public ClipboardContentNormalizer()
        : this(new ClipboardTextNormalizer(), new ClipboardFileListNormalizer())
    {
    }

    public ClipboardContentNormalizer(
        ClipboardTextNormalizer text,
        ClipboardFileListNormalizer files)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public ClipboardNormalizedContent? Normalize(ClipboardCaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Formats.Any(static format =>
                format.RepresentationKind is
                    ClipboardRepresentationKind.Png or
                    ClipboardRepresentationKind.DibV5 or
                    ClipboardRepresentationKind.Dib or
                    ClipboardRepresentationKind.Bitmap))
        {
            return null;
        }

        if (capture.Formats.Any(static format =>
                format.RepresentationKind is ClipboardRepresentationKind.FileDrop &&
                format.CaptureState is not ClipboardFormatCaptureState.NotRequested))
        {
            return _files.Normalize(capture);
        }

        if (capture.Formats.Any(static format =>
                format.RepresentationKind is
                    ClipboardRepresentationKind.FileGroupDescriptor or
                    ClipboardRepresentationKind.FileGroupDescriptorW or
                    ClipboardRepresentationKind.FileContents or
                    ClipboardRepresentationKind.ShellIdList))
        {
            return null;
        }

        return _text.NormalizeText(capture);
    }
}
