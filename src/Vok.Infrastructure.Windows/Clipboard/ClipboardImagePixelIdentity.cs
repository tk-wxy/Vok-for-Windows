using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Vok.Infrastructure.Windows.Clipboard;

internal readonly record struct ClipboardImagePixelIdentityResult(
    string SemanticHash,
    bool HasAlpha);

internal static class ClipboardImagePixelIdentity
{
    private static ReadOnlySpan<byte> Version => "Vok.Image.Pixel.v1"u8;

    private static ReadOnlySpan<byte> PixelFormatTag =>
        "BGRA8.STRAIGHT.TOPDOWN"u8;

    private const int PixelBandBytes = 1024 * 1024;

    public static ClipboardImagePixelIdentityResult Compute(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var stride = checked(source.PixelWidth * 4);
        var rowsPerBand = Math.Max(1, PixelBandBytes / stride);
        var buffer = ArrayPool<byte>.Shared.Rent(
            checked(stride * Math.Min(rowsPerBand, source.PixelHeight)));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Version);
            Span<byte> dimensions = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(dimensions, source.PixelWidth);
            BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], source.PixelHeight);
            hash.AppendData(dimensions);
            hash.AppendData(PixelFormatTag);

            var hasAlpha = false;
            for (var y = 0; y < source.PixelHeight; y += rowsPerBand)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowCount = Math.Min(rowsPerBand, source.PixelHeight - y);
                var byteCount = checked(stride * rowCount);
                source.CopyPixels(
                    new Int32Rect(0, y, source.PixelWidth, rowCount),
                    buffer,
                    stride,
                    offset: 0);
                var pixels = buffer.AsSpan(0, byteCount);
                for (var index = 3; index < pixels.Length; index += 4)
                {
                    hasAlpha |= pixels[index] != byte.MaxValue;
                }

                hash.AppendData(pixels);
            }

            return new ClipboardImagePixelIdentityResult(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                hasAlpha);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
