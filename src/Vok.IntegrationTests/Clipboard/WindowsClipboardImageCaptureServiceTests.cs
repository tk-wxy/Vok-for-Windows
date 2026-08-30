using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardImageCaptureServiceTests
{
    [Fact]
    public async Task CaptureNormalizesCommitsAndReleasesTransferredOwnership()
    {
        var directory = CreateTestDirectory();
        WindowsClipboardImageExecutor? normalizer = null;
        ClipboardImageAssetCoordinator? assets = null;
        SqliteClipboardHistoryRepository? repository = null;
        try
        {
            var options = new ClipboardImageCaptureOptions();
            repository = new SqliteClipboardHistoryRepository(
                Path.Combine(directory, "vok.db"));
            var temporaryStore = new ClipboardImageTemporaryStore(
                Path.Combine(directory, "normalization"));
            normalizer = new WindowsClipboardImageExecutor(
                options,
                new ClipboardImageNormalizer(options),
                temporaryStore,
                TimeSpan.FromSeconds(5));
            assets = new ClipboardImageAssetCoordinator(
                repository,
                new ContentAddressedClipboardAssetStore(
                    Path.Combine(directory, "assets"),
                    options),
                options);
            var service = new WindowsClipboardImageCaptureService(normalizer, assets);
            var png = EncodePng(
                2,
                2,
                [
                    0, 0, 255, 255,
                    0, 255, 0, 255,
                    255, 0, 0, 128,
                    255, 255, 255, 255,
                ]);
            var reservation = new TrackingReservation(png.Length + 16);
            var format = new ClipboardFormatSnapshot(
                0,
                "PNG",
                ClipboardRepresentationKind.Png,
                ClipboardFormatRole.Content,
                ClipboardFormatCaptureState.Captured,
                png,
                SourceByteLength: png.Length,
                ImageHeader: new ClipboardImageHeader(2, 2, 16),
                Reservation: reservation);
            var capture = new ClipboardCaptureResult(
                101,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Captured,
                [format]);

            var entry = await service.CaptureAsync(
                capture,
                TestContext.Current.CancellationToken);

            Assert.Equal(ClipboardContentKind.Image, entry.Kind);
            Assert.Equal(2, entry.Image?.PixelWidth);
            Assert.Equal(2, entry.Image?.PixelHeight);
            Assert.Equal(ClipboardAssetState.Ready, entry.Image?.PrimaryPng?.State);
            Assert.Equal(ClipboardAssetState.Ready, entry.Image?.ThumbnailPng?.State);
            Assert.True(capture.IsDisposed);
            Assert.True(format.IsDisposed);
            Assert.Equal(1, reservation.DisposeCount);
            Assert.Equal(0, normalizer.OutstandingJobCount);
            Assert.Equal(0, normalizer.OutstandingTemporaryLeaseCount);
            Assert.Equal(0, assets.OutstandingJobCount);

            var page = await repository.LoadPageAsync(
                after: null,
                pageSize: 10,
                TestContext.Current.CancellationToken);
            Assert.Equal(entry, Assert.Single(page.Items));
        }
        finally
        {
            assets?.Dispose();
            normalizer?.Dispose();
            repository?.Dispose();
            DeleteTestDirectory(directory);
        }
    }

    private static byte[] EncodePng(int width, int height, byte[] bgraPixels)
    {
        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            bgraPixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TrackingReservation(long reservedBytes)
        : IClipboardPayloadReservation
    {
        public long ReservedBytes { get; } = reservedBytes;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
