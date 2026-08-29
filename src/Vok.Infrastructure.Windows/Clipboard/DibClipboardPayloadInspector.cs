using System.Buffers.Binary;
using System.IO;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class DibClipboardPayloadInspector
{
    private const uint BiRgb = 0;
    private const uint BiBitFields = 3;
    private const uint BiAlphaBitFields = 6;
    private const uint ProfileLinked = 0x4c494e4b;
    private const uint ProfileEmbedded = 0x4d424544;
    private const uint LcsSrgb = 0x73524742;
    private const uint LcsWindowsColorSpace = 0x57696e20;

    public static ClipboardImagePayloadInfo Inspect(
        Stream stream,
        long expectedLength,
        ClipboardImageCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanRead || !stream.CanSeek || expectedLength < 40 ||
            stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The DIB payload stream is not a complete seekable payload.");
        }

        stream.Position = 0;
        Span<byte> sizeBytes = stackalloc byte[4];
        stream.ReadExactly(sizeBytes);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
        if (headerSize is not (40 or 108 or 124))
        {
            throw new NotSupportedException(
                "Only BITMAPINFOHEADER, BITMAPV4HEADER, and BITMAPV5HEADER DIBs are supported.");
        }

        if (headerSize > expectedLength)
        {
            throw new InvalidDataException("The DIB header extends beyond the payload.");
        }

        Span<byte> header = stackalloc byte[124];
        sizeBytes.CopyTo(header);
        stream.ReadExactly(header.Slice(4, checked((int)headerSize - 4)));

        var width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var declaredImageBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var xPixelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        var yPixelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        if (width <= 0 || storedHeight is 0 or int.MinValue || planes != 1 ||
            bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new InvalidDataException(
                "The DIB dimensions, planes, or bit depth are invalid.");
        }

        var height = Math.Abs(storedHeight);
        if (!options.TryCalculateReservationBytes(
                expectedLength,
                width,
                height,
                out _,
                out _))
        {
            throw new InvalidDataException("The DIB exceeds the configured image limits.");
        }

        ValidateCompression(bitsPerPixel, compression, storedHeight < 0);
        var externalMaskCount = headerSize == 40
            ? compression switch
            {
                BiBitFields => 3,
                BiAlphaBitFields => 4,
                _ => 0,
            }
            : 0;
        var externalMaskBytes = checked(externalMaskCount * sizeof(uint));
        var masks = ReadMasks(
            stream,
            header,
            headerSize,
            externalMaskCount,
            bitsPerPixel,
            compression);
        ValidateMasks(masks, bitsPerPixel, compression);

        var colorSpaceType = headerSize >= 108
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[56..])
            : LcsSrgb;
        var profileOffset = headerSize == 124
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[112..])
            : 0;
        var profileSize = headerSize == 124
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[116..])
            : 0;
        var reserved = headerSize == 124
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[120..])
            : 0;
        if (colorSpaceType is not (
                0 or
                LcsSrgb or
                LcsWindowsColorSpace or
                ProfileLinked or
                ProfileEmbedded))
        {
            throw new NotSupportedException("The DIB color space type is not supported.");
        }

        if (reserved != 0)
        {
            throw new InvalidDataException("The BITMAPV5HEADER reserved field is nonzero.");
        }

        if (colorSpaceType == ProfileLinked)
        {
            throw new NotSupportedException(
                "Linked DIB color profiles are not opened from clipboard payloads.");
        }

        var paletteEntries = CalculatePaletteEntries(bitsPerPixel, colorsUsed);
        long paletteBytes;
        long pixelOffset;
        long stride;
        long pixelBytes;
        try
        {
            paletteBytes = checked((long)paletteEntries * 4);
            pixelOffset = checked((long)headerSize + externalMaskBytes + paletteBytes);
            stride = checked((((long)width * bitsPerPixel + 31) / 32) * 4);
            pixelBytes = checked(stride * height);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The DIB layout overflows its byte range.", exception);
        }

        if (declaredImageBytes != 0 && declaredImageBytes != pixelBytes)
        {
            throw new InvalidDataException(
                "The DIB declared image length does not match its uncompressed stride.");
        }

        long pixelEnd;
        try
        {
            pixelEnd = checked(pixelOffset + pixelBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The DIB pixel range overflows.", exception);
        }

        var packedPixelEnd = colorSpaceType == ProfileEmbedded
            ? profileOffset
            : expectedLength;
        var redundantMaskCount = headerSize >= 108
            ? compression switch
            {
                BiBitFields => 3,
                BiAlphaBitFields => 4,
                _ => 0,
            }
            : 0;
        var redundantMaskBytes = checked(redundantMaskCount * sizeof(uint));
        if (redundantMaskBytes > 0 &&
            packedPixelEnd - pixelEnd == redundantMaskBytes &&
            RedundantMasksMatch(stream, headerSize, masks, redundantMaskCount))
        {
            pixelOffset = checked(pixelOffset + redundantMaskBytes);
            pixelEnd = checked(pixelEnd + redundantMaskBytes);
        }

        if (pixelOffset > expectedLength || pixelEnd > expectedLength)
        {
            throw new InvalidDataException("The DIB palette, masks, or pixels are truncated.");
        }

        var requiresColorContext = headerSize >= 108 && colorSpaceType == 0;

        if (headerSize == 124)
        {
            if (colorSpaceType == ProfileEmbedded)
            {
                if (profileSize == 0 || profileOffset != pixelEnd ||
                    (profileOffset & 3) != 0)
                {
                    throw new InvalidDataException(
                        "The packed DIB embedded profile is misplaced or empty.");
                }

                long profileEnd;
                try
                {
                    profileEnd = checked((long)profileOffset + profileSize);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        "The DIB profile range overflows.",
                        exception);
                }

                if (profileEnd != expectedLength)
                {
                    throw new InvalidDataException(
                        "The packed DIB embedded profile is truncated or has trailing bytes.");
                }

                requiresColorContext = true;
            }
            else if (pixelEnd != expectedLength)
            {
                throw new InvalidDataException("The packed DIB has unexplained trailing bytes.");
            }
        }
        else if (pixelEnd != expectedLength)
        {
            throw new InvalidDataException("The packed DIB has unexplained trailing bytes.");
        }

        if (pixelOffset > uint.MaxValue || expectedLength + 14 > uint.MaxValue)
        {
            throw new InvalidDataException("The DIB cannot be represented as a BMP stream.");
        }

        var bitmapPrefix = CreateBitmapFilePrefix(expectedLength, pixelOffset);
        stream.Position = 0;
        return new ClipboardImagePayloadInfo(
            width,
            height,
            ToDpiMilli(xPixelsPerMeter),
            ToDpiMilli(yPixelsPerMeter),
            pixelOffset,
            pixelBytes,
            requiresColorContext,
            bitmapPrefix);
    }

    private static void ValidateCompression(
        ushort bitsPerPixel,
        uint compression,
        bool topDown)
    {
        var supported = bitsPerPixel switch
        {
            1 or 4 or 8 or 24 => compression == BiRgb,
            16 or 32 => compression is BiRgb or BiBitFields or BiAlphaBitFields,
            _ => false,
        };
        if (!supported)
        {
            throw new NotSupportedException(
                "The DIB compression and bit-depth combination is not supported.");
        }

        if (topDown && compression is not (BiRgb or BiBitFields))
        {
            throw new NotSupportedException(
                "Top-down DIBs must use BI_RGB or BI_BITFIELDS.");
        }
    }

    private static DibMasks ReadMasks(
        Stream stream,
        ReadOnlySpan<byte> header,
        uint headerSize,
        int externalMaskCount,
        ushort bitsPerPixel,
        uint compression)
    {
        if (compression == BiRgb)
        {
            return bitsPerPixel switch
            {
                16 => new DibMasks(0x7c00, 0x03e0, 0x001f, 0),
                24 or 32 => new DibMasks(0x00ff0000, 0x0000ff00, 0x000000ff, 0),
                _ => default,
            };
        }

        Span<byte> maskBytes = stackalloc byte[16];
        if (headerSize == 40)
        {
            stream.ReadExactly(maskBytes[..checked(externalMaskCount * 4)]);
        }
        else
        {
            header.Slice(40, 16).CopyTo(maskBytes);
        }

        return new DibMasks(
            BinaryPrimitives.ReadUInt32LittleEndian(maskBytes),
            BinaryPrimitives.ReadUInt32LittleEndian(maskBytes[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(maskBytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(maskBytes[12..]));
    }

    private static void ValidateMasks(
        DibMasks masks,
        ushort bitsPerPixel,
        uint compression)
    {
        if (compression == BiRgb || bitsPerPixel < 16)
        {
            return;
        }

        if (masks.Red == 0 || masks.Green == 0 || masks.Blue == 0 ||
            !IsContiguous(masks.Red) || !IsContiguous(masks.Green) ||
            !IsContiguous(masks.Blue) ||
            (masks.Alpha != 0 && !IsContiguous(masks.Alpha)))
        {
            throw new InvalidDataException("The DIB bitfield masks are empty or non-contiguous.");
        }

        var all = masks.Red | masks.Green | masks.Blue | masks.Alpha;
        var validBits = bitsPerPixel == 32
            ? uint.MaxValue
            : (1u << bitsPerPixel) - 1;
        if ((all & ~validBits) != 0 ||
            (masks.Red & masks.Green) != 0 ||
            (masks.Red & masks.Blue) != 0 ||
            (masks.Green & masks.Blue) != 0 ||
            (masks.Alpha & (masks.Red | masks.Green | masks.Blue)) != 0 ||
            (compression == BiAlphaBitFields && masks.Alpha == 0))
        {
            throw new InvalidDataException("The DIB bitfield masks overlap or exceed the pixel depth.");
        }
    }

    private static bool RedundantMasksMatch(
        Stream stream,
        uint headerSize,
        DibMasks masks,
        int maskCount)
    {
        Span<byte> bytes = stackalloc byte[16];
        stream.Position = headerSize;
        stream.ReadExactly(bytes[..checked(maskCount * sizeof(uint))]);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == masks.Red &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == masks.Green &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == masks.Blue &&
            (maskCount == 3 ||
             BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) == masks.Alpha);
    }

    private static uint CalculatePaletteEntries(
        ushort bitsPerPixel,
        uint colorsUsed)
    {
        if (bitsPerPixel <= 8)
        {
            var maximum = 1u << bitsPerPixel;
            if (colorsUsed > maximum)
            {
                throw new InvalidDataException("The DIB palette contains too many entries.");
            }

            return colorsUsed == 0 ? maximum : colorsUsed;
        }

        return colorsUsed;
    }

    private static bool IsContiguous(uint mask)
    {
        var shifted = mask >> System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (shifted & (shifted + 1)) == 0;
    }

    private static byte[] CreateBitmapFilePrefix(
        long dibLength,
        long pixelOffset)
    {
        var prefix = new byte[14];
        prefix[0] = (byte)'B';
        prefix[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(
            prefix.AsSpan(2),
            checked((uint)(dibLength + prefix.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            prefix.AsSpan(10),
            checked((uint)(pixelOffset + prefix.Length)));
        return prefix;
    }

    private static int? ToDpiMilli(int pixelsPerMeter)
    {
        if (pixelsPerMeter <= 0)
        {
            return null;
        }

        var value = pixelsPerMeter * 25.4d;
        return value <= int.MaxValue
            ? checked((int)Math.Round(value, MidpointRounding.AwayFromZero))
            : null;
    }

    private readonly record struct DibMasks(
        uint Red,
        uint Green,
        uint Blue,
        uint Alpha);
}
