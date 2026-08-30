using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardImageReplayEncoder
{
    internal const int BitmapV5HeaderBytes = 124;
    private const uint BiBitFields = 3;
    private const uint LcsSrgb = 0x73524742;
    private const uint LcsGmImages = 4;
    private const uint RedMask = 0x00ff0000;
    private const uint GreenMask = 0x0000ff00;
    private const uint BlueMask = 0x000000ff;
    private const uint AlphaMask = 0xff000000;

    private readonly ClipboardImageCaptureOptions _options;

    public ClipboardImageReplayEncoder(ClipboardImageCaptureOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ClipboardImageReplayMaterialization Materialize(
        ClipboardReplayPayload descriptor,
        IClipboardPayloadLease canonicalPng,
        ClipboardImageTemporaryStore temporaryStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(canonicalPng);
        ArgumentNullException.ThrowIfNull(temporaryStore);
        var image = ValidateDescriptor(descriptor, canonicalPng.Length);

        IClipboardPayloadLease? ownedCanonical = canonicalPng;
        IClipboardPayloadLease? dibV5 = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource normalized;
            try
            {
                normalized = DecodeCanonicalPng(
                    canonicalPng,
                    image.PixelWidth,
                    image.PixelHeight,
                    cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new ClipboardImageReplayIntegrityException(
                    "The canonical clipboard PNG failed pixel replay validation.",
                    exception);
            }

            var identity = ClipboardImagePixelIdentity.Compute(
                normalized,
                cancellationToken);
            if (!string.Equals(
                    identity.SemanticHash,
                    descriptor.SemanticHash,
                    StringComparison.Ordinal) ||
                identity.HasAlpha != image.HasAlpha)
            {
                throw new ClipboardImageReplayIntegrityException(
                    "The canonical clipboard image pixels do not match their replay descriptor.");
            }

            var dibLength = checked(
                BitmapV5HeaderBytes + (long)image.PixelWidth * image.PixelHeight * 4);
            dibV5 = temporaryStore.CreateDibV5(
                dibLength,
                output => WriteDibV5(
                    normalized,
                    image.DpiXMilli,
                    image.DpiYMilli,
                    output,
                    cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            var materialization = new ClipboardImageReplayMaterialization(
                descriptor.SemanticHash,
                image.PixelWidth,
                image.PixelHeight,
                image.HasAlpha,
                image.DpiXMilli,
                image.DpiYMilli,
                ownedCanonical,
                dibV5);
            ownedCanonical = null;
            dibV5 = null;
            return materialization;
        }
        finally
        {
            dibV5?.Dispose();
            ownedCanonical?.Dispose();
        }
    }

    private ClipboardImageMetadata ValidateDescriptor(
        ClipboardReplayPayload descriptor,
        long canonicalPngLength)
    {
        if (descriptor.Kind is not ClipboardContentKind.Image ||
            descriptor.Representations.Count != 0 ||
            descriptor.FileIntent is not null ||
            descriptor.FileReferences.Count != 0 ||
            descriptor.Image is not { } image ||
            image.PrimaryPng is not
            {
                State: ClipboardAssetState.Ready,
                Revision: >= 1,
            })
        {
            throw new InvalidDataException(
                "The clipboard image replay descriptor is incomplete or unavailable.");
        }

        if (canonicalPngLength is < 1 || canonicalPngLength > _options.MaximumInputBytes ||
            image.PixelWidth is < 1 || image.PixelWidth > _options.MaximumDimensionPixels ||
            image.PixelHeight is < 1 || image.PixelHeight > _options.MaximumDimensionPixels ||
            image.DpiXMilli is <= 0 || image.DpiYMilli is <= 0 ||
            image.DpiXMilli.HasValue != image.DpiYMilli.HasValue)
        {
            throw new InvalidDataException(
                "The clipboard image replay descriptor exceeds its configured bounds.");
        }

        try
        {
            var pixels = checked((long)image.PixelWidth * image.PixelHeight);
            var decodedBytes = checked(pixels * 4);
            var replayBytes = checked(
                canonicalPngLength + BitmapV5HeaderBytes + decodedBytes);
            if (pixels > _options.MaximumPixelCount ||
                decodedBytes > _options.MaximumCanonicalBytes ||
                replayBytes > _options.MaximumReservedBytes)
            {
                throw new InvalidDataException(
                    "The clipboard image replay envelope exceeds its configured bounds.");
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The clipboard image replay dimensions overflow their configured bounds.",
                exception);
        }

        return image;
    }

    private static BitmapSource DecodeCanonicalPng(
        IClipboardPayloadLease canonicalPng,
        int expectedPixelWidth,
        int expectedPixelHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = canonicalPng.OpenRead();
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1)
            {
                throw new InvalidDataException(
                    "The canonical clipboard PNG must decode to exactly one frame.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != expectedPixelWidth ||
                frame.PixelHeight != expectedPixelHeight)
            {
                throw new InvalidDataException(
                    "The canonical clipboard PNG dimensions changed during replay.");
            }

            BitmapSource normalized = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(
                    frame,
                    PixelFormats.Bgra32,
                    destinationPalette: null,
                    alphaThreshold: 0);
            if (normalized.CanFreeze)
            {
                normalized.Freeze();
            }

            return normalized;
        }
        catch (OperationCanceledException)
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
            InvalidOperationException or
            NotSupportedException)
        {
            throw new InvalidDataException(
                "The Windows image codec rejected the canonical clipboard PNG.",
                exception);
        }
    }

    private static void WriteDibV5(
        BitmapSource source,
        int? dpiXMilli,
        int? dpiYMilli,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            throw new InvalidDataException(
                "A replay DIBV5 requires straight-alpha BGRA8 pixels.");
        }

        var stride = checked(source.PixelWidth * 4);
        var imageBytes = checked((uint)((long)stride * source.PixelHeight));
        Span<byte> header = stackalloc byte[BitmapV5HeaderBytes];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, BitmapV5HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], source.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], source.PixelHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiBitFields);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[24..],
            ToPixelsPerMeter(dpiXMilli));
        BinaryPrimitives.WriteInt32LittleEndian(
            header[28..],
            ToPixelsPerMeter(dpiYMilli));
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], RedMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], GreenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], BlueMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], AlphaMask);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], LcsSrgb);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], LcsGmImages);
        output.Write(header);

        var row = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            for (var y = source.PixelHeight - 1; y >= 0; y--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyPixels(
                    new Int32Rect(0, y, source.PixelWidth, 1),
                    row,
                    stride,
                    offset: 0);
                output.Write(row, 0, stride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    private static int ToPixelsPerMeter(int? dpiMilli)
    {
        if (dpiMilli is null)
        {
            return 0;
        }

        var pixelsPerMeter = Math.Round(
            dpiMilli.Value * (10d / 254d),
            MidpointRounding.AwayFromZero);
        if (pixelsPerMeter is < 1 or > int.MaxValue)
        {
            throw new InvalidDataException(
                "The clipboard image DPI cannot be represented in DIBV5 metadata.");
        }

        return checked((int)pixelsPerMeter);
    }
}

internal sealed class ClipboardImageReplayIntegrityException : Exception
{
    public ClipboardImageReplayIntegrityException(string message)
        : base(message)
    {
    }

    public ClipboardImageReplayIntegrityException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
