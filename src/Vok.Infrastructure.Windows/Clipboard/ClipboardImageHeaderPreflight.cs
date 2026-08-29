using System.Buffers.Binary;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class ClipboardImageHeaderPreflight
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static int RequiredHeaderBytes(ClipboardRepresentationKind kind) => kind switch
    {
        ClipboardRepresentationKind.Png => 29,
        ClipboardRepresentationKind.DibV5 => 124,
        ClipboardRepresentationKind.Dib => 40,
        _ => 0,
    };

    public static bool TryInspect(
        ClipboardRepresentationKind kind,
        ReadOnlySpan<byte> payloadHeader,
        long sourceByteLength,
        ClipboardImageCaptureOptions options,
        out ClipboardImageHeader? imageHeader,
        out bool exceededLimits,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        imageHeader = null;
        exceededLimits = false;
        error = null;
        int width;
        int height;
        switch (kind)
        {
            case ClipboardRepresentationKind.Png:
                if (!TryInspectPng(payloadHeader, out width, out height, out error))
                {
                    return false;
                }

                break;
            case ClipboardRepresentationKind.DibV5:
            case ClipboardRepresentationKind.Dib:
                if (!TryInspectDib(
                        kind,
                        payloadHeader,
                        sourceByteLength,
                        out width,
                        out height,
                        out error))
                {
                    return false;
                }

                break;
            default:
                error = "The clipboard representation is not a packed image payload.";
                return false;
        }

        if (!options.TryCalculateReservationBytes(
                sourceByteLength,
                width,
                height,
                out var canonicalBytes,
                out _))
        {
            exceededLimits = true;
            error = "The image exceeds its input, dimension, pixel, canonical, or per-job reservation limit.";
            return false;
        }

        imageHeader = new ClipboardImageHeader(width, height, canonicalBytes);
        return true;
    }

    private static bool TryInspectPng(
        ReadOnlySpan<byte> header,
        out int width,
        out int height,
        out string? error)
    {
        width = 0;
        height = 0;
        error = null;
        if (header.Length < 29 || !header[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13 ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            error = "The PNG signature or first IHDR chunk is invalid.";
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var bitDepth = header[24];
        var colorType = header[25];
        if (rawWidth is 0 or > int.MaxValue || rawHeight is 0 or > int.MaxValue ||
            header[26] != 0 || header[27] != 0 || header[28] > 1 ||
            !IsValidPngColorDepth(colorType, bitDepth))
        {
            error = "The PNG IHDR fields are invalid or unsupported.";
            return false;
        }

        width = (int)rawWidth;
        height = (int)rawHeight;
        return true;
    }

    private static bool TryInspectDib(
        ClipboardRepresentationKind kind,
        ReadOnlySpan<byte> header,
        long sourceByteLength,
        out int width,
        out int height,
        out string? error)
    {
        width = 0;
        height = 0;
        error = null;
        var minimumHeaderBytes = RequiredHeaderBytes(kind);
        if (header.Length < minimumHeaderBytes)
        {
            error = $"The {kind} payload is shorter than its required header.";
            return false;
        }

        var declaredHeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (declaredHeaderBytes < minimumHeaderBytes ||
            declaredHeaderBytes > sourceByteLength)
        {
            error = $"The {kind} header length is outside the payload.";
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        if (rawWidth <= 0 || storedHeight is 0 or int.MinValue || planes != 1)
        {
            error = $"The {kind} dimensions or plane count are invalid.";
            return false;
        }

        width = rawWidth;
        height = Math.Abs(storedHeight);
        return true;
    }

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
}
