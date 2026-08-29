using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class PngClipboardPayloadInspector
{
    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private static readonly uint[] CrcTable = CreateCrcTable();

    public static ClipboardImagePayloadInfo Inspect(
        Stream stream,
        long expectedLength,
        ClipboardImageCaptureOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        EnsureSeekablePayload(stream, expectedLength);

        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature))
        {
            throw new InvalidDataException("The PNG signature is invalid.");
        }

        var sawHeader = false;
        var sawPalette = false;
        var sawImageData = false;
        var endedImageData = false;
        var sawEnd = false;
        var sawPhysicalDimensions = false;
        var sawEmbeddedColorProfile = false;
        var width = 0;
        var height = 0;
        int? dpiXMilli = null;
        int? dpiYMilli = null;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> storedCrcBytes = stackalloc byte[4];
            while (!sawEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadExactlyOrInvalid(stream, chunkHeader, "PNG chunk header");
                var dataLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
                var type = chunkHeader[4..8];
                ValidateChunkType(type);
                if (dataLength > expectedLength - stream.Position - 4)
                {
                    throw new InvalidDataException(
                        "A PNG chunk extends beyond the payload.");
                }

                var typeCode = BinaryPrimitives.ReadUInt32BigEndian(type);
                if (!sawHeader && typeCode != Chunk("IHDR"u8))
                {
                    throw new InvalidDataException("IHDR must be the first PNG chunk.");
                }

                if (typeCode is var animationChunk &&
                    (animationChunk == Chunk("acTL"u8) ||
                     animationChunk == Chunk("fcTL"u8) ||
                     animationChunk == Chunk("fdAT"u8)))
                {
                    throw new NotSupportedException(
                        "Animated or multi-frame PNG payloads are not supported.");
                }

                if (IsUnknownCriticalChunk(typeCode))
                {
                    throw new NotSupportedException(
                        "The PNG contains an unsupported critical chunk.");
                }

                var capturedLength = typeCode switch
                {
                    var code when code == Chunk("IHDR"u8) => 13,
                    var code when code == Chunk("pHYs"u8) => 9,
                    _ => 0,
                };
                if (capturedLength != 0 && dataLength != capturedLength)
                {
                    throw new InvalidDataException(
                        "A fixed-size PNG chunk has an invalid length.");
                }

                var capturedBytes = capturedLength == 0
                    ? Array.Empty<byte>()
                    : new byte[capturedLength];
                var captured = capturedBytes.AsSpan();
                var crc = uint.MaxValue;
                crc = UpdateCrc(crc, type);
                var remaining = (long)dataLength;
                var capturedOffset = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(remaining, buffer.Length);
                    ReadExactlyOrInvalid(
                        stream,
                        buffer.AsSpan(0, count),
                        "PNG chunk data");
                    crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                    if (!captured.IsEmpty)
                    {
                        buffer.AsSpan(0, count).CopyTo(captured[capturedOffset..]);
                        capturedOffset += count;
                    }

                    remaining -= count;
                }

                ReadExactlyOrInvalid(stream, storedCrcBytes, "PNG chunk CRC");
                var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(storedCrcBytes);
                if (~crc != storedCrc)
                {
                    throw new InvalidDataException("A PNG chunk CRC is invalid.");
                }

                if (typeCode == Chunk("IHDR"u8))
                {
                    if (sawHeader)
                    {
                        throw new InvalidDataException("The PNG contains multiple IHDR chunks.");
                    }

                    (width, height) = ParseHeader(captured, options, expectedLength);
                    sawHeader = true;
                }
                else if (typeCode == Chunk("PLTE"u8))
                {
                    if (sawPalette || sawImageData || dataLength is 0 or > 768 || dataLength % 3 != 0)
                    {
                        throw new InvalidDataException("The PNG palette is invalid or misplaced.");
                    }

                    sawPalette = true;
                }
                else if (typeCode == Chunk("IDAT"u8))
                {
                    if (endedImageData)
                    {
                        throw new InvalidDataException("PNG IDAT chunks must be consecutive.");
                    }

                    sawImageData = true;
                }
                else
                {
                    endedImageData |= sawImageData;
                    if (typeCode == Chunk("pHYs"u8))
                    {
                        if (sawPhysicalDimensions || sawImageData)
                        {
                            throw new InvalidDataException("The PNG pHYs chunk is duplicated or misplaced.");
                        }

                        sawPhysicalDimensions = true;
                        if (captured[8] == 1)
                        {
                            dpiXMilli = ToDpiMilli(
                                BinaryPrimitives.ReadUInt32BigEndian(captured));
                            dpiYMilli = ToDpiMilli(
                                BinaryPrimitives.ReadUInt32BigEndian(captured[4..]));
                        }
                    }
                    else if (typeCode == Chunk("iCCP"u8))
                    {
                        if (sawEmbeddedColorProfile || sawImageData || dataLength == 0)
                        {
                            throw new InvalidDataException("The PNG iCCP chunk is duplicated or misplaced.");
                        }

                        sawEmbeddedColorProfile = true;
                    }
                    else if (typeCode == Chunk("IEND"u8))
                    {
                        if (dataLength != 0 || !sawImageData)
                        {
                            throw new InvalidDataException("The PNG IEND chunk is invalid or premature.");
                        }

                        sawEnd = true;
                    }
                }
            }

            if (stream.Position != expectedLength)
            {
                throw new InvalidDataException("The PNG has trailing bytes after IEND.");
            }

            stream.Position = 0;
            return new ClipboardImagePayloadInfo(
                width,
                height,
                dpiXMilli,
                dpiYMilli,
                PixelOffset: 0,
                PixelByteLength: 0,
                RequiresColorContext: sawEmbeddedColorProfile,
                BitmapFilePrefix: null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (int Width, int Height) ParseHeader(
        ReadOnlySpan<byte> header,
        ClipboardImageCaptureOptions options,
        long expectedLength)
    {
        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(header);
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var bitDepth = header[8];
        var colorType = header[9];
        if (rawWidth is 0 or > int.MaxValue || rawHeight is 0 or > int.MaxValue ||
            header[10] != 0 || header[11] != 0 || header[12] > 1 ||
            !IsValidColorDepth(colorType, bitDepth))
        {
            throw new InvalidDataException("The PNG IHDR fields are invalid.");
        }

        var width = (int)rawWidth;
        var height = (int)rawHeight;
        if (!options.TryCalculateReservationBytes(
                expectedLength,
                width,
                height,
                out _,
                out _))
        {
            throw new InvalidDataException("The PNG exceeds the configured image limits.");
        }

        return (width, height);
    }

    private static bool IsValidColorDepth(byte colorType, byte bitDepth) =>
        colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };

    private static bool IsUnknownCriticalChunk(uint type) =>
        type != Chunk("IHDR"u8) &&
        type != Chunk("PLTE"u8) &&
        type != Chunk("IDAT"u8) &&
        type != Chunk("IEND"u8) &&
        (type & 0x20000000u) == 0;

    private static void ValidateChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'a' and <= (byte)'z'))
            {
                throw new InvalidDataException("A PNG chunk type contains invalid bytes.");
            }
        }

        if ((type[2] & 0x20) != 0)
        {
            throw new InvalidDataException("A PNG chunk uses the reserved lowercase type bit.");
        }
    }

    private static uint Chunk(ReadOnlySpan<byte> type) =>
        BinaryPrimitives.ReadUInt32BigEndian(type);

    private static void EnsureSeekablePayload(Stream stream, long expectedLength)
    {
        if (!stream.CanRead || !stream.CanSeek || expectedLength < 1 ||
            stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The PNG payload stream is not a complete seekable payload.");
        }

        stream.Position = 0;
    }

    private static void ReadExactlyOrInvalid(
        Stream stream,
        Span<byte> destination,
        string component)
    {
        try
        {
            stream.ReadExactly(destination);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"The {component} is truncated.", exception);
        }
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xedb88320u ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static int? ToDpiMilli(uint pixelsPerMeter)
    {
        if (pixelsPerMeter == 0)
        {
            return null;
        }

        var value = pixelsPerMeter * 25.4d;
        return value <= int.MaxValue
            ? checked((int)Math.Round(value, MidpointRounding.AwayFromZero))
            : null;
    }
}
