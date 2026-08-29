using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vok.Core.Clipboard;

namespace Vok.ClipboardImageRecorder;

internal sealed record ControlledImageSamplePaths(
    string DirectoryPath,
    string TransparentPngPath,
    string Transparent8KPngPath,
    string HtmlPath);

internal static class ControlledImageSamples
{
    private const int DpiPixelsPerMeter = 3780;

    public static ControlledImageSamplePaths WriteToTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.ClipboardImageRecorder",
            $"samples-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(directory);

        var transparentPng = Path.Combine(directory, "known-transparent-256.png");
        var transparent8KPng = Path.Combine(directory, "known-transparent-8k.png");
        var html = Path.Combine(directory, "samples.html");
        File.WriteAllBytes(transparentPng, CreatePng(256, 256, cellSize: 32));
        File.WriteAllBytes(transparent8KPng, CreatePng(7680, 4320, cellSize: 128));
        File.WriteAllText(
            html,
            """
            <!doctype html>
            <html lang="en">
            <meta charset="utf-8">
            <title>Vok M2.3 controlled transparent PNG samples</title>
            <style>
              body { font: 16px system-ui; margin: 24px; }
              .checker { background-color: #eee; background-image: linear-gradient(45deg,#bbb 25%,transparent 25%),linear-gradient(-45deg,#bbb 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#bbb 75%),linear-gradient(-45deg,transparent 75%,#bbb 75%); background-size: 24px 24px; background-position: 0 0,0 12px,12px -12px,-12px 0; padding: 16px; margin-bottom: 24px; }
              img { display: block; max-width: 100%; height: auto; }
            </style>
            <h1>Vok M2.3 controlled samples</h1>
            <p>Right-click the requested image and choose Copy image. Both PNGs contain real varying alpha and declare approximately 96 DPI.</p>
            <h2>Known transparent 256×256 PNG</h2>
            <div class="checker"><img src="known-transparent-256.png" alt="Known transparent 256 by 256 PNG"></div>
            <h2>Known transparent 7680×4320 PNG</h2>
            <div class="checker"><img src="known-transparent-8k.png" alt="Known transparent 8K PNG"></div>
            </html>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ControlledImageSamplePaths(
            directory,
            transparentPng,
            transparent8KPng,
            html);
    }

    public static void RunSelfTest()
    {
        var png = CreatePng(2, 2, cellSize: 1);
        var metadata = ClipboardImageMetadataInspector.Inspect(
            ClipboardRepresentationKind.Png,
            png);
        if (metadata is null ||
            !metadata.IsHeaderValid ||
            metadata.PixelWidth != 2 ||
            metadata.PixelHeight != 2 ||
            metadata.HasDeclaredAlpha != true ||
            metadata.PngXPixelsPerUnit != DpiPixelsPerMeter ||
            metadata.PngPhysicalUnit != 1)
        {
            throw new InvalidOperationException("Controlled PNG sample self-test failed.");
        }
    }

    private static byte[] CreatePng(int width, int height, int cellSize)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        WriteChunk(output, "sRGB", [0]);
        var physical = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(physical, DpiPixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), DpiPixelsPerMeter);
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
                for (var x = 0; x < width; x++)
                {
                    var lightCell = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                    var pixel = 1 + x * 4;
                    row[pixel] = lightCell ? (byte)0x20 : (byte)0xf0;
                    row[pixel + 1] = (byte)(0x40 + (y % 128));
                    row[pixel + 2] = lightCell ? (byte)0xe0 : (byte)0x30;
                    row[pixel + 3] = lightCell ? (byte)0x50 : (byte)0xd0;
                }

                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
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
