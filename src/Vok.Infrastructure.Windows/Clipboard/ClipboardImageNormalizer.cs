using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardImageNormalizer : IClipboardImageNormalizationEngine
{
    private readonly ClipboardImageCaptureOptions _options;

    public ClipboardImageNormalizer(ClipboardImageCaptureOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ClipboardImageNormalizationResult Normalize(
        ClipboardCaptureResult capture,
        ClipboardImageTemporaryStore temporaryStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(temporaryStore);
        cancellationToken.ThrowIfCancellationRequested();

        var format = SelectSource(capture);
        var payloadInfo = InspectPayload(format, cancellationToken);
        var captureHeader = format.ImageHeader ?? throw new InvalidDataException(
            "The captured image is missing its preflight dimensions.");
        if (captureHeader.PixelWidth != payloadInfo.PixelWidth ||
            captureHeader.PixelHeight != payloadInfo.PixelHeight ||
            captureHeader.CanonicalByteLength !=
                checked((long)payloadInfo.PixelWidth * payloadInfo.PixelHeight * 4))
        {
            throw new InvalidDataException(
                "The strict image dimensions do not match the captured preflight header.");
        }

        var normalized = DecodeAndNormalize(
            format,
            payloadInfo,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = ClipboardImagePixelIdentity.Compute(
            normalized,
            cancellationToken);
        var thumbnail = CreateThumbnail(normalized);

        IClipboardPayloadLease? canonicalLease = null;
        IClipboardPayloadLease? thumbnailLease = null;
        try
        {
            canonicalLease = temporaryStore.CreatePng(
                "normalized",
                output => EncodeMetadataFreePng(normalized, output));
            cancellationToken.ThrowIfCancellationRequested();
            thumbnailLease = temporaryStore.CreatePng(
                "thumbnail",
                output => EncodeMetadataFreePng(thumbnail, output));
            cancellationToken.ThrowIfCancellationRequested();

            var result = new ClipboardImageNormalizationResult(
                format.RepresentationKind,
                identity.SemanticHash,
                normalized.PixelWidth,
                normalized.PixelHeight,
                identity.HasAlpha,
                payloadInfo.DpiXMilli,
                payloadInfo.DpiYMilli,
                thumbnail.PixelWidth,
                thumbnail.PixelHeight,
                canonicalLease,
                thumbnailLease);
            canonicalLease = null;
            thumbnailLease = null;
            return result;
        }
        finally
        {
            thumbnailLease?.Dispose();
            canonicalLease?.Dispose();
        }
    }

    private static ClipboardFormatSnapshot SelectSource(
        ClipboardCaptureResult capture)
    {
        if (capture.Disposition is not ClipboardCaptureDisposition.Captured)
        {
            throw new InvalidDataException(
                "Only a captured clipboard image can be normalized.");
        }

        var candidates = capture.Formats
            .Where(static format =>
                format.CaptureState is ClipboardFormatCaptureState.Captured &&
                format.RepresentationKind is
                    ClipboardRepresentationKind.Png or
                    ClipboardRepresentationKind.DibV5 or
                    ClipboardRepresentationKind.Dib)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                "A clipboard image job must contain exactly one captured packed payload.");
        }

        var candidate = candidates[0];
        if (candidate.PayloadByteLength is < 1 ||
            candidate.PayloadByteLength != candidate.SourceByteLength)
        {
            throw new InvalidDataException(
                "The clipboard image payload length is incomplete or inconsistent.");
        }

        return candidate;
    }

    private ClipboardImagePayloadInfo InspectPayload(
        ClipboardFormatSnapshot format,
        CancellationToken cancellationToken)
    {
        using var stream = format.OpenPayloadReadStream();
        return format.RepresentationKind switch
        {
            ClipboardRepresentationKind.Png =>
                PngClipboardPayloadInspector.Inspect(
                    stream,
                    format.PayloadByteLength,
                    _options,
                    cancellationToken),
            ClipboardRepresentationKind.DibV5 or ClipboardRepresentationKind.Dib =>
                DibClipboardPayloadInspector.Inspect(
                    stream,
                    format.PayloadByteLength,
                    _options),
            _ => throw new NotSupportedException(
                "The clipboard image representation is not supported."),
        };
    }

    private static BitmapSource DecodeAndNormalize(
        ClipboardFormatSnapshot format,
        ClipboardImagePayloadInfo payloadInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var payload = format.OpenPayloadReadStream();
            using var codecStream = payloadInfo.BitmapFilePrefix is null
                ? payload
                : new SeekablePrefixStream(
                    payloadInfo.BitmapFilePrefix,
                    payload,
                    leaveOpen: true);
            var decoder = BitmapDecoder.Create(
                codecStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1)
            {
                throw new NotSupportedException(
                    "Only single-frame clipboard images are supported.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != payloadInfo.PixelWidth ||
                frame.PixelHeight != payloadInfo.PixelHeight)
            {
                throw new InvalidDataException(
                    "The codec dimensions do not match the strict payload layout.");
            }

            return ConvertToStraightSrgbBgra32(
                frame,
                payloadInfo.RequiresColorContext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            FileFormatException or
            IOException or
            ArgumentException or
            InvalidOperationException)
        {
            throw new InvalidDataException(
                "The Windows image codec rejected the clipboard payload.",
                exception);
        }
    }

    private static BitmapSource ConvertToStraightSrgbBgra32(
        BitmapFrame frame,
        bool requiresColorContext)
    {
        ColorContext? sourceContext = null;
        ReadOnlyCollection<ColorContext>? colorContexts = null;
        try
        {
            colorContexts = frame.ColorContexts;
            sourceContext = colorContexts?.FirstOrDefault();
        }
        catch (Exception exception) when (exception is
            FileFormatException or
            NotSupportedException)
        {
            throw new InvalidDataException(
                "The clipboard image color profile is invalid.",
                exception);
        }

        if (requiresColorContext && sourceContext is null)
        {
            throw new InvalidDataException(
                "The embedded clipboard image color profile could not be decoded.");
        }

        BitmapSource converted;
        if (sourceContext is not null)
        {
            try
            {
                converted = new ColorConvertedBitmap(
                    frame,
                    sourceContext,
                    new ColorContext(PixelFormats.Bgra32),
                    PixelFormats.Bgra32);
            }
            catch (Exception exception) when (exception is
                FileFormatException or
                NotSupportedException or
                ArgumentException or
                InvalidOperationException)
            {
                throw new InvalidDataException(
                    "The clipboard image could not be converted to sRGB.",
                    exception);
            }
        }
        else
        {
            converted = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(
                    frame,
                    PixelFormats.Bgra32,
                    destinationPalette: null,
                    alphaThreshold: 0);
        }

        if (converted.Format != PixelFormats.Bgra32)
        {
            converted = new FormatConvertedBitmap(
                converted,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
        }

        if (converted.CanFreeze)
        {
            converted.Freeze();
        }

        return converted;
    }

    private BitmapSource CreateThumbnail(BitmapSource source)
    {
        var maximumEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maximumEdge <= _options.ThumbnailMaximumEdgePixels)
        {
            return source;
        }

        int width;
        int height;
        if (source.PixelWidth >= source.PixelHeight)
        {
            width = _options.ThumbnailMaximumEdgePixels;
            height = Math.Max(
                1,
                checked((int)Math.Round(
                    source.PixelHeight * (width / (double)source.PixelWidth),
                    MidpointRounding.AwayFromZero)));
        }
        else
        {
            height = _options.ThumbnailMaximumEdgePixels;
            width = Math.Max(
                1,
                checked((int)Math.Round(
                    source.PixelWidth * (height / (double)source.PixelHeight),
                    MidpointRounding.AwayFromZero)));
        }

        var transformed = new TransformedBitmap(
            source,
            new ScaleTransform(
                width / (double)source.PixelWidth,
                height / (double)source.PixelHeight));
        if (transformed.PixelWidth > _options.ThumbnailMaximumEdgePixels ||
            transformed.PixelHeight > _options.ThumbnailMaximumEdgePixels)
        {
            throw new InvalidDataException(
                "The image scaler exceeded the configured thumbnail bound.");
        }

        if (transformed.CanFreeze)
        {
            transformed.Freeze();
        }

        return transformed;
    }

    private static void EncodeMetadataFreePng(BitmapSource source, Stream output)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            source,
            thumbnail: null,
            metadata: null,
            colorContexts: null));
        encoder.Save(output);
    }

}
