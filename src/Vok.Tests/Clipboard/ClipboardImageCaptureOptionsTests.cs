using Vok.Core.Clipboard;
using Xunit;

namespace Vok.Tests.Clipboard;

public sealed class ClipboardImageCaptureOptionsTests
{
    [Fact]
    public void DefaultsMatchTheGateZeroCapacityDecision()
    {
        var options = new ClipboardImageCaptureOptions();

        Assert.Equal(192L * 1024 * 1024, options.MaximumInputBytes);
        Assert.Equal(8192, options.MaximumDimensionPixels);
        Assert.Equal(41_943_040, options.MaximumPixelCount);
        Assert.Equal(160L * 1024 * 1024, options.MaximumCanonicalBytes);
        Assert.Equal(2 * 1024 * 1024, options.InlinePayloadBytes);
        Assert.Equal(1024 * 1024, options.LeaseCopyBufferBytes);
        Assert.Equal(4, options.ImageWorkerCapacity);
        Assert.Equal(384L * 1024 * 1024, options.MaximumReservedBytes);
        Assert.Equal(512, options.ThumbnailMaximumEdgePixels);
        Assert.Equal(32L * 1024 * 1024, options.ThumbnailCacheBytes);
        Assert.Equal(1024 * 1024, options.ThumbnailEncodedOverheadBytes);
    }

    [Fact]
    public void ExactPixelAndCanonicalBoundaryIsAcceptedAndPlusOneIsRejected()
    {
        var options = new ClipboardImageCaptureOptions();

        Assert.True(options.TryCalculateReservationBytes(
            options.MaximumInputBytes,
            8192,
            5120,
            out var canonicalBytes,
            out var reservedBytes));
        Assert.Equal(options.MaximumCanonicalBytes, canonicalBytes);
        Assert.Equal(options.MaximumInputBytes + canonicalBytes, reservedBytes);

        Assert.False(options.TryCalculateReservationBytes(
            1,
            8192,
            5121,
            out _,
            out _));
        Assert.False(options.TryCalculateReservationBytes(
            1,
            8193,
            1,
            out _,
            out _));
    }

    [Fact]
    public void PerJobReservationBoundaryIsCheckedWithoutOverflow()
    {
        var options = new ClipboardImageCaptureOptions(
            maximumInputBytes: 81,
            maximumDimensionPixels: 5,
            maximumPixelCount: 5,
            maximumCanonicalBytes: 20,
            inlinePayloadBytes: 16,
            leaseCopyBufferBytes: 8,
            maximumReservedBytes: 100);

        Assert.True(options.TryCalculateReservationBytes(
            80,
            5,
            1,
            out var canonicalBytes,
            out var reservedBytes));
        Assert.Equal(20, canonicalBytes);
        Assert.Equal(100, reservedBytes);
        Assert.False(options.TryCalculateReservationBytes(
            81,
            5,
            1,
            out _,
            out _));
        Assert.False(options.TryCalculateReservationBytes(
            long.MaxValue,
            int.MaxValue,
            int.MaxValue,
            out _,
            out _));
    }

    [Fact]
    public void CaptureResultDisposesLeaseAndReservationExactlyOnce()
    {
        var lease = new TrackingLease([1, 2, 3]);
        var reservation = new TrackingReservation(15);
        var format = new ClipboardFormatSnapshot(
            0,
            "PNG",
            ClipboardRepresentationKind.Png,
            ClipboardFormatRole.Content,
            ClipboardFormatCaptureState.Captured,
            ReadOnlyMemory<byte>.Empty,
            SourceByteLength: 3,
            ImageHeader: new ClipboardImageHeader(2, 2, 16),
            PayloadLease: lease,
            Reservation: reservation);
        var capture = new ClipboardCaptureResult(
            1,
            DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Captured,
            [format]);

        Assert.Equal(ClipboardPayloadStorageKind.LeasedBlob, format.PayloadStorageKind);
        Assert.Equal(3, format.PayloadByteLength);
        Assert.Equal(15, format.ReservedByteLength);
        using (var stream = format.OpenPayloadReadStream())
        {
            Assert.Equal([1, 2, 3], ReadAll(stream));
        }

        capture.Dispose();
        capture.Dispose();

        Assert.True(capture.IsDisposed);
        Assert.True(format.IsDisposed);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, reservation.DisposeCount);
        Assert.Throws<ObjectDisposedException>(format.OpenPayloadReadStream);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed class TrackingLease(byte[] bytes) : IClipboardPayloadLease
    {
        public long Length => bytes.Length;

        public int DisposeCount { get; private set; }

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingReservation(long reservedBytes)
        : IClipboardPayloadReservation
    {
        public long ReservedBytes { get; } = reservedBytes;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
