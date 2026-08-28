using System.Buffers.Binary;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ClipboardFileListNormalizer
{
    private const uint DropEffectNone = 0;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint TransferIntentMask = DropEffectCopy | DropEffectMove;
    private readonly ClipboardFileNormalizationOptions _options;

    public ClipboardFileListNormalizer()
        : this(new ClipboardFileNormalizationOptions())
    {
    }

    public ClipboardFileListNormalizer(ClipboardFileNormalizationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ClipboardFileListContent? Normalize(ClipboardCaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Disposition is not ClipboardCaptureDisposition.Captured)
        {
            return null;
        }

        var fileDrop = capture.Formats
            .Where(static format =>
                format.RepresentationKind is ClipboardRepresentationKind.FileDrop)
            .OrderBy(static format => format.SourceOrder)
            .FirstOrDefault();
        if (fileDrop is null ||
            fileDrop.CaptureState is not ClipboardFormatCaptureState.Captured ||
            !ClipboardFileDropCodec.TryDecode(fileDrop.Payload, _options, out var files) ||
            !TryReadIntent(capture.Formats, out var intent))
        {
            return null;
        }

        var preview = files.Count == 1
            ? files[0].DisplayName
            : $"{files[0].DisplayName} + 另外 {files.Count - 1} 项";
        return new ClipboardFileListContent(
            ClipboardFileSemanticIdentity.Compute(intent, files),
            intent,
            preview,
            files);
    }

    private static bool TryReadIntent(
        IEnumerable<ClipboardFormatSnapshot> formats,
        out ClipboardFileTransferIntent intent)
    {
        intent = ClipboardFileTransferIntent.Unknown;
        var effect = formats
            .Where(static format =>
                format.RepresentationKind is
                    ClipboardRepresentationKind.PreferredDropEffect)
            .OrderBy(static format => format.SourceOrder)
            .FirstOrDefault();
        if (effect is null)
        {
            return true;
        }

        if (effect.CaptureState is not ClipboardFormatCaptureState.Captured ||
            effect.Payload.Length < sizeof(uint))
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(effect.Payload.Span);
        var transferIntent = value & TransferIntentMask;
        intent = transferIntent switch
        {
            DropEffectCopy => ClipboardFileTransferIntent.Copy,
            DropEffectMove => ClipboardFileTransferIntent.Move,
            _ => ClipboardFileTransferIntent.Unknown,
        };

        if (transferIntent is DropEffectCopy or DropEffectMove)
        {
            return true;
        }

        return value is DropEffectNone;
    }
}
