using System.Buffers.Binary;
using Vok.Core.Clipboard;

namespace Vok.ClipboardImageRecorder;

internal sealed record ClipboardImageHeaderMetadata(
    string Container,
    bool IsHeaderValid,
    string? ValidationError,
    uint? HeaderBytes,
    int? PixelWidth,
    int? PixelHeight,
    string? Orientation,
    ushort? Planes,
    ushort? BitsPerPixel,
    uint? Compression,
    uint? DeclaredImageBytes,
    int? XPelsPerMeter,
    int? YPelsPerMeter,
    double? DpiX,
    double? DpiY,
    bool? HasDeclaredAlpha,
    string? RedMask,
    string? GreenMask,
    string? BlueMask,
    string? AlphaMask,
    string? ColorSpace,
    uint? ProfileOffset,
    uint? ProfileBytes,
    byte? PngBitDepth,
    byte? PngColorType,
    byte? PngInterlace)
{
    public uint? PngXPixelsPerUnit { get; init; }

    public uint? PngYPixelsPerUnit { get; init; }

    public byte? PngPhysicalUnit { get; init; }

    public bool? PngHasTransparencyChunk { get; init; }

    public byte? PngSrgbRenderingIntent { get; init; }
}

internal static class ClipboardImageMetadataInspector
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static ClipboardImageHeaderMetadata? Inspect(
        ClipboardRepresentationKind kind,
        ReadOnlySpan<byte> payload) => kind switch
        {
            ClipboardRepresentationKind.Png => InspectPng(payload),
            ClipboardRepresentationKind.Dib or ClipboardRepresentationKind.DibV5 =>
                InspectDib(kind, payload),
            _ => null,
        };

    public static void RunSelfTest()
    {
        var png = new byte[54];
        PngSignature.CopyTo(png);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 1920);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1080);
        png[24] = 8;
        png[25] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33), 9);
        "pHYs"u8.CopyTo(png.AsSpan(37));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(41), 3780);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(45), 3780);
        png[49] = 1;
        var pngMetadata = InspectPng(png);
        Require(
            pngMetadata.IsHeaderValid &&
            pngMetadata.PixelWidth == 1920 &&
            pngMetadata.PixelHeight == 1080 &&
            pngMetadata.HasDeclaredAlpha == true &&
            pngMetadata.PngXPixelsPerUnit == 3780 &&
            pngMetadata.DpiX == 96.012,
            "PNG IHDR inspection failed.");

        var dib = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 124);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 3840);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -2160);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), 0x73524742);
        var dibMetadata = InspectDib(ClipboardRepresentationKind.DibV5, dib);
        Require(
            dibMetadata.IsHeaderValid &&
            dibMetadata.PixelWidth == 3840 &&
            dibMetadata.PixelHeight == 2160 &&
            dibMetadata.Orientation == "TopDown" &&
            dibMetadata.AlphaMask == "0xFF000000",
            "DIBV5 inspection failed.");
    }

    private static ClipboardImageHeaderMetadata InspectPng(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 29 || !payload[..8].SequenceEqual(PngSignature))
        {
            return Invalid("PNG", "Missing PNG signature or IHDR.");
        }

        var ihdrLength = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        if (ihdrLength != 13 || !payload.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return Invalid("PNG", "The first PNG chunk is not a 13-byte IHDR.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(payload[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(payload[20..]);
        var bitDepth = payload[24];
        var colorType = payload[25];
        var compression = payload[26];
        var filter = payload[27];
        var interlace = payload[28];
        var ancillary = InspectPngAncillaryChunks(payload);
        var valid = width is > 0 and <= int.MaxValue &&
                    height is > 0 and <= int.MaxValue &&
                    compression == 0 &&
                    filter == 0 &&
                    interlace <= 1 &&
                    IsValidPngColorDepth(colorType, bitDepth);
        return new ClipboardImageHeaderMetadata(
            "PNG",
            valid,
            valid ? null : "PNG IHDR fields are invalid or unsupported.",
            13,
            width <= int.MaxValue ? (int)width : null,
            height <= int.MaxValue ? (int)height : null,
            "TopDown",
            null,
            null,
            null,
            null,
            null,
            null,
            ancillary.PhysicalUnit == 1 && ancillary.XPixelsPerUnit.HasValue
                ? ToDpi(ancillary.XPixelsPerUnit.Value)
                : null,
            ancillary.PhysicalUnit == 1 && ancillary.YPixelsPerUnit.HasValue
                ? ToDpi(ancillary.YPixelsPerUnit.Value)
                : null,
            colorType is 4 or 6 || ancillary.HasTransparencyChunk,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            bitDepth,
            colorType,
            interlace)
        {
            PngXPixelsPerUnit = ancillary.XPixelsPerUnit,
            PngYPixelsPerUnit = ancillary.YPixelsPerUnit,
            PngPhysicalUnit = ancillary.PhysicalUnit,
            PngHasTransparencyChunk = ancillary.HasTransparencyChunk,
            PngSrgbRenderingIntent = ancillary.SrgbRenderingIntent,
        };
    }

    private static PngAncillaryMetadata InspectPngAncillaryChunks(
        ReadOnlySpan<byte> payload)
    {
        uint? xPixelsPerUnit = null;
        uint? yPixelsPerUnit = null;
        byte? physicalUnit = null;
        var hasTransparencyChunk = false;
        byte? srgbRenderingIntent = null;
        var offset = 8;
        while (offset <= payload.Length - 12)
        {
            var chunkBytes = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
            if (chunkBytes > int.MaxValue)
            {
                break;
            }

            var chunkEnd = (long)offset + 12 + chunkBytes;
            if (chunkEnd > payload.Length)
            {
                break;
            }

            var type = payload.Slice(offset + 4, 4);
            var data = payload.Slice(offset + 8, (int)chunkBytes);
            if (type.SequenceEqual("pHYs"u8) && data.Length == 9)
            {
                xPixelsPerUnit = BinaryPrimitives.ReadUInt32BigEndian(data);
                yPixelsPerUnit = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                physicalUnit = data[8];
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                hasTransparencyChunk = true;
            }
            else if (type.SequenceEqual("sRGB"u8) && data.Length == 1)
            {
                srgbRenderingIntent = data[0];
            }

            offset = (int)chunkEnd;
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        return new PngAncillaryMetadata(
            xPixelsPerUnit,
            yPixelsPerUnit,
            physicalUnit,
            hasTransparencyChunk,
            srgbRenderingIntent);
    }

    private static ClipboardImageHeaderMetadata InspectDib(
        ClipboardRepresentationKind kind,
        ReadOnlySpan<byte> payload)
    {
        var container = kind is ClipboardRepresentationKind.DibV5 ? "DIBV5" : "DIB";
        if (payload.Length < 40)
        {
            return Invalid(container, "The DIB payload is shorter than BITMAPINFOHEADER.");
        }

        var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (headerBytes is < 40 or > int.MaxValue || headerBytes > payload.Length)
        {
            return Invalid(container, "The DIB header size is outside the payload.", headerBytes);
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        var imageBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]);
        var xPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
        var yPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
        var valid = width > 0 && storedHeight is not (0 or int.MinValue) && planes == 1;
        int? absoluteHeight = storedHeight == int.MinValue ? null : Math.Abs(storedHeight);

        uint? redMask = null;
        uint? greenMask = null;
        uint? blueMask = null;
        uint? alphaMask = null;
        if (headerBytes >= 108)
        {
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]);
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[44..]);
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[48..]);
            alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[52..]);
        }
        else if (headerBytes == 40 && compression is 3 or 6 && payload.Length >= 52)
        {
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]);
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[44..]);
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[48..]);
            if (compression == 6 && payload.Length >= 56)
            {
                alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(payload[52..]);
            }
        }

        uint? colorSpace = null;
        uint? profileOffset = null;
        uint? profileBytes = null;
        if (headerBytes >= 108)
        {
            colorSpace = BinaryPrimitives.ReadUInt32LittleEndian(payload[56..]);
        }

        if (headerBytes >= 124)
        {
            profileOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload[112..]);
            profileBytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[116..]);
        }

        bool? hasDeclaredAlpha = alphaMask.HasValue
            ? alphaMask.Value != 0
            : bitsPerPixel == 32 && compression == 0
                ? false
                : null;

        return new ClipboardImageHeaderMetadata(
            container,
            valid,
            valid ? null : "DIB dimensions or plane count are invalid.",
            headerBytes,
            width > 0 ? width : null,
            absoluteHeight,
            storedHeight < 0 ? "TopDown" : "BottomUp",
            planes,
            bitsPerPixel,
            compression,
            imageBytes,
            xPelsPerMeter,
            yPelsPerMeter,
            ToDpi(xPelsPerMeter),
            ToDpi(yPelsPerMeter),
            hasDeclaredAlpha,
            Hex(redMask),
            Hex(greenMask),
            Hex(blueMask),
            Hex(alphaMask),
            Hex(colorSpace),
            profileOffset,
            profileBytes,
            null,
            null,
            null);
    }

    private static ClipboardImageHeaderMetadata Invalid(
        string container,
        string error,
        uint? headerBytes = null) => new(
            container,
            false,
            error,
            headerBytes,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static bool IsValidPngColorDepth(byte colorType, byte bitDepth) =>
        colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };

    private static double? ToDpi(int pelsPerMeter) => pelsPerMeter > 0
        ? Math.Round(pelsPerMeter * 0.0254, 3, MidpointRounding.AwayFromZero)
        : null;

    private static double ToDpi(uint pixelsPerMeter) =>
        Math.Round(pixelsPerMeter * 0.0254, 3, MidpointRounding.AwayFromZero);

    private static string? Hex(uint? value) => value.HasValue
        ? $"0x{value.Value:X8}"
        : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private readonly record struct PngAncillaryMetadata(
        uint? XPixelsPerUnit,
        uint? YPixelsPerUnit,
        byte? PhysicalUnit,
        bool HasTransparencyChunk,
        byte? SrgbRenderingIntent);
}
