namespace Vok.Core.Clipboard;

public sealed class ClipboardImageCaptureOptions
{
    public const long DefaultMaximumInputBytes = 192L * 1024 * 1024;
    public const int DefaultMaximumDimensionPixels = 8192;
    public const long DefaultMaximumPixelCount = 40L * 1024 * 1024;
    public const long DefaultMaximumCanonicalBytes = 160L * 1024 * 1024;
    public const int DefaultInlinePayloadBytes = 2 * 1024 * 1024;
    public const int DefaultLeaseCopyBufferBytes = 1024 * 1024;
    public const int DefaultImageWorkerCapacity = 4;
    public const long DefaultMaximumReservedBytes = 384L * 1024 * 1024;
    public const int DefaultThumbnailMaximumEdgePixels = 512;
    public const long DefaultThumbnailCacheBytes = 32L * 1024 * 1024;
    public const int DefaultThumbnailEncodedOverheadBytes = 1024 * 1024;

    public ClipboardImageCaptureOptions(
        long maximumInputBytes = DefaultMaximumInputBytes,
        int maximumDimensionPixels = DefaultMaximumDimensionPixels,
        long maximumPixelCount = DefaultMaximumPixelCount,
        long maximumCanonicalBytes = DefaultMaximumCanonicalBytes,
        int inlinePayloadBytes = DefaultInlinePayloadBytes,
        int leaseCopyBufferBytes = DefaultLeaseCopyBufferBytes,
        int imageWorkerCapacity = DefaultImageWorkerCapacity,
        long maximumReservedBytes = DefaultMaximumReservedBytes,
        int thumbnailMaximumEdgePixels = DefaultThumbnailMaximumEdgePixels,
        long thumbnailCacheBytes = DefaultThumbnailCacheBytes,
        int thumbnailEncodedOverheadBytes = DefaultThumbnailEncodedOverheadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumInputBytes, int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDimensionPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPixelCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCanonicalBytes, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(inlinePayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inlinePayloadBytes, maximumInputBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseCopyBufferBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(leaseCopyBufferBytes, maximumInputBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWorkerCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumReservedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thumbnailMaximumEdgePixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thumbnailCacheBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thumbnailEncodedOverheadBytes, 1);

        MaximumInputBytes = maximumInputBytes;
        MaximumDimensionPixels = maximumDimensionPixels;
        MaximumPixelCount = maximumPixelCount;
        MaximumCanonicalBytes = maximumCanonicalBytes;
        InlinePayloadBytes = inlinePayloadBytes;
        LeaseCopyBufferBytes = leaseCopyBufferBytes;
        ImageWorkerCapacity = imageWorkerCapacity;
        MaximumReservedBytes = maximumReservedBytes;
        ThumbnailMaximumEdgePixels = thumbnailMaximumEdgePixels;
        ThumbnailCacheBytes = thumbnailCacheBytes;
        ThumbnailEncodedOverheadBytes = thumbnailEncodedOverheadBytes;
    }

    public long MaximumInputBytes { get; }

    public int MaximumDimensionPixels { get; }

    public long MaximumPixelCount { get; }

    public long MaximumCanonicalBytes { get; }

    public int InlinePayloadBytes { get; }

    public int LeaseCopyBufferBytes { get; }

    public int ImageWorkerCapacity { get; }

    public long MaximumReservedBytes { get; }

    public int ThumbnailMaximumEdgePixels { get; }

    public long ThumbnailCacheBytes { get; }

    public int ThumbnailEncodedOverheadBytes { get; }

    public bool TryCalculateReservationBytes(
        long inputByteLength,
        int pixelWidth,
        int pixelHeight,
        out long canonicalByteLength,
        out long reservedByteLength)
    {
        canonicalByteLength = 0;
        reservedByteLength = 0;
        if (inputByteLength is < 1 || inputByteLength > MaximumInputBytes ||
            pixelWidth is < 1 || pixelWidth > MaximumDimensionPixels ||
            pixelHeight is < 1 || pixelHeight > MaximumDimensionPixels)
        {
            return false;
        }

        try
        {
            var pixels = checked((long)pixelWidth * pixelHeight);
            if (pixels > MaximumPixelCount)
            {
                return false;
            }

            var canonical = checked(pixels * 4);
            if (canonical > MaximumCanonicalBytes)
            {
                return false;
            }

            var reserved = checked(inputByteLength + canonical);
            if (reserved > MaximumReservedBytes)
            {
                return false;
            }

            canonicalByteLength = canonical;
            reservedByteLength = reserved;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
