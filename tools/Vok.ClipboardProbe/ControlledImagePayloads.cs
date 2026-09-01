using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Vok.ClipboardProbe;

internal static class ControlledImagePayloads
{
    public const int Width = 2;
    public const int Height = 2;

    private static readonly byte[] RgbaPixels =
    [
        0xff, 0x00, 0x00, 0xff,
        0x00, 0xff, 0x00, 0x80,
        0x00, 0x00, 0xff, 0x40,
        0xff, 0xff, 0xff, 0xff,
    ];

    public static byte[] CreatePng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        var scanlines = new byte[checked(Height * (1 + Width * 4))];
        for (var row = 0; row < Height; row++)
        {
            var destination = row * (1 + Width * 4);
            scanlines[destination] = 0;
            RgbaPixels.AsSpan(row * Width * 4, Width * 4)
                .CopyTo(scanlines.AsSpan(destination + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static byte[] CreatePatternPng(
        int width,
        int height,
        int cellSize,
        byte[]? embeddedIccProfile = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        if (embeddedIccProfile is { Length: > 0 })
        {
            WriteChunk(output, "iCCP", CreateIccChunk(embeddedIccProfile));
        }
        else
        {
            WriteChunk(output, "sRGB", [0]);
        }

        var physical = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(physical, 3780);
        BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), 3780);
        physical[8] = 1;
        WriteChunk(output, "pHYs", physical);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            var row = new byte[checked(1 + width * 4)];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                WritePatternRgbaRow(row.AsSpan(1), width, y, cellSize);
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static byte[] CreateDibV5()
    {
        const int headerBytes = 124;
        var stride = Width * 4;
        var imageBytes = stride * Height;
        var payload = new byte[headerBytes + imageBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), Height);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28), 3780);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(56), 0x73524742);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(108), 4);

        WriteBottomUpBgra(payload.AsSpan(headerBytes), stride);
        return payload;
    }

    public static byte[] CreatePatternDibV5(
        int width,
        int height,
        int cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);

        const int headerBytes = 124;
        var stride = checked(width * 4);
        var imageBytes = checked(stride * height);
        var payload = new byte[checked(headerBytes + imageBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28), 3780);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(56), 0x73524742);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(108), 4);

        var rgbaRow = new byte[stride];
        for (var storedRow = 0; storedRow < height; storedRow++)
        {
            var sourceY = height - storedRow - 1;
            WritePatternRgbaRow(rgbaRow, width, sourceY, cellSize);
            var destination = payload.AsSpan(headerBytes + storedRow * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var source = x * 4;
                destination[source] = rgbaRow[source + 2];
                destination[source + 1] = rgbaRow[source + 1];
                destination[source + 2] = rgbaRow[source];
                destination[source + 3] = rgbaRow[source + 3];
            }
        }

        return payload;
    }

    public static byte[] CreateDib24()
    {
        const int headerBytes = 40;
        var stride = ((Width * 24 + 31) / 32) * 4;
        var imageBytes = stride * Height;
        var payload = new byte[headerBytes + imageBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), -Height);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28), 3780);

        var pixels = payload.AsSpan(headerBytes);
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                var source = (row * Width + column) * 4;
                var destination = row * stride + column * 3;
                pixels[destination] = RgbaPixels[source + 2];
                pixels[destination + 1] = RgbaPixels[source + 1];
                pixels[destination + 2] = RgbaPixels[source];
            }
        }

        return payload;
    }

    public static byte[] CreateBottomUpBgraPixels()
    {
        var pixels = new byte[Width * Height * 4];
        WriteBottomUpBgra(pixels, Width * 4);
        return pixels;
    }

    private static void WriteBottomUpBgra(Span<byte> destination, int stride)
    {
        for (var outputRow = 0; outputRow < Height; outputRow++)
        {
            var sourceRow = Height - outputRow - 1;
            for (var column = 0; column < Width; column++)
            {
                var source = (sourceRow * Width + column) * 4;
                var target = outputRow * stride + column * 4;
                destination[target] = RgbaPixels[source + 2];
                destination[target + 1] = RgbaPixels[source + 1];
                destination[target + 2] = RgbaPixels[source];
                destination[target + 3] = RgbaPixels[source + 3];
            }
        }
    }

    private static byte[] CreateIccChunk(byte[] profile)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            zlib.Write(profile);
        }

        var name = Encoding.ASCII.GetBytes("Vok sRGB");
        var compressedBytes = compressed.ToArray();
        var payload = new byte[checked(name.Length + 2 + compressedBytes.Length)];
        name.CopyTo(payload, 0);
        payload[name.Length] = 0;
        payload[name.Length + 1] = 0;
        compressedBytes.CopyTo(payload, name.Length + 2);
        return payload;
    }

    private static void WritePatternRgbaRow(
        Span<byte> destination,
        int width,
        int y,
        int cellSize)
    {
        for (var x = 0; x < width; x++)
        {
            var lightCell = ((x / cellSize) + (y / cellSize)) % 2 == 0;
            var pixel = x * 4;
            destination[pixel] = lightCell ? (byte)0x20 : (byte)0xf0;
            destination[pixel + 1] = (byte)(0x40 + (y % 128));
            destination[pixel + 2] = lightCell ? (byte)0xe0 : (byte)0x30;
            destination[pixel + 3] = lightCell ? (byte)0x50 : (byte)0xd0;
        }
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> integer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)data.Length));
        output.Write(integer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = ComputeCrc(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(integer, crc);
        output.Write(integer);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        return ~crc;
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xedb88320u ^ (crc >> 1)
                    : crc >> 1;
            }
        }
    }
}
