using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardImageReplayMaterializerTests
{
    [Fact]
    public async Task MaterializeRevalidatesCanonicalPixelsAndCreatesBottomUpDibV5()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);

        using var materialization = await fixture.Coordinator.MaterializeAsync(
            descriptor,
            TestContext.Current.CancellationToken);

        Assert.Equal(entry.SemanticHash, materialization.SemanticHash);
        Assert.Equal(2, materialization.PixelWidth);
        Assert.Equal(2, materialization.PixelHeight);
        Assert.True(materialization.HasAlpha);
        var canonicalPixels = DecodeBgra32(
            materialization.OpenCanonicalPngReadStream(),
            expectedWidth: 2,
            expectedHeight: 2);
        var dib = ReadAll(materialization.OpenDibV5ReadStream());
        Assert.Equal(140, dib.Length);
        var inspected = DibClipboardPayloadInspector.Inspect(
            new MemoryStream(dib, writable: false),
            dib.LongLength,
            new ClipboardImageCaptureOptions());
        Assert.Equal(2, inspected.PixelWidth);
        Assert.Equal(2, inspected.PixelHeight);
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dib));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(20)));
        Assert.Equal(
            ToPixelsPerMeter(descriptor.Image?.DpiXMilli),
            BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(24)));
        Assert.Equal(
            ToPixelsPerMeter(descriptor.Image?.DpiYMilli),
            BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(28)));
        Assert.Equal(0x00ff0000u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(40)));
        Assert.Equal(0x0000ff00u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(44)));
        Assert.Equal(0x000000ffu, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(48)));
        Assert.Equal(0xff000000u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52)));
        Assert.Equal(0x73524742u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(56)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(108)));
        Assert.True(canonicalPixels.AsSpan(8, 8).SequenceEqual(dib.AsSpan(124, 8)));
        Assert.True(canonicalPixels.AsSpan(0, 8).SequenceEqual(dib.AsSpan(132, 8)));

        materialization.Dispose();
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);
        Assert.Throws<ObjectDisposedException>(
            materialization.OpenCanonicalPngReadStream);
    }

    [Fact]
    public async Task SemanticMismatchQuarantinesPrimaryAndReleasesReplayBlob()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);
        var mismatched = descriptor with { SemanticHash = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Coordinator.MaterializeAsync(
                mismatched,
                TestContext.Current.CancellationToken));

        var stored = await fixture.Repository.LoadAssetAsync(
            primary.Hash,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ClipboardAssetState.Quarantined, stored.State);
        Assert.Equal(primary.Revision + 1, stored.Revision);
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);
    }

    [Fact]
    public async Task StalePrimaryRevisionFailsWithoutQuarantiningCurrentAsset()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);
        var stale = descriptor with
        {
            Image = descriptor.Image! with
            {
                PrimaryPng = primary with { Revision = primary.Revision + 1 },
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Coordinator.MaterializeAsync(
                stale,
                TestContext.Current.CancellationToken));

        var stored = await fixture.Repository.LoadAssetAsync(
            primary.Hash,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ClipboardAssetState.Ready, stored.State);
        Assert.Equal(primary.Revision, stored.Revision);
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);
    }

    [Fact]
    public async Task PreCanceledReplayCreatesNoBlobAndLeavesPrimaryReady()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.MaterializeAsync(descriptor, cancellation.Token));

        var stored = await fixture.Repository.LoadAssetAsync(
            primary.Hash,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ClipboardAssetState.Ready, stored.State);
        Assert.Equal(primary.Revision, stored.Revision);
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);
    }

    [Fact]
    public async Task ReplayStagingFailureDoesNotMisclassifyPrimaryAsCorrupt()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);
        fixture.ReplayTemporaryStore.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fixture.Coordinator.MaterializeAsync(
                descriptor,
                TestContext.Current.CancellationToken));

        var stored = await fixture.Repository.LoadAssetAsync(
            primary.Hash,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ClipboardAssetState.Ready, stored.State);
        Assert.Equal(primary.Revision, stored.Revision);
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);
    }

    [Fact]
    public async Task ExportMaterializationRevalidatesPixelsWithoutCreatingDibLease()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);

        using var materialization = await fixture.Coordinator
            .MaterializeForExportAsync(
                descriptor,
                TestContext.Current.CancellationToken);

        Assert.Equal(primary.Hash, materialization.AssetHash);
        Assert.Equal(entry.SemanticHash, materialization.SemanticHash);
        Assert.Equal(2, materialization.PixelWidth);
        Assert.Equal(2, materialization.PixelHeight);
        Assert.True(materialization.HasAlpha);
        using var stream = materialization.OpenCanonicalPngReadStream();
        Assert.Equal(
            primary.Hash,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        Assert.Equal(0, fixture.Coordinator.OutstandingReplayLeaseCount);

        materialization.Dispose();
        Assert.Throws<ObjectDisposedException>(
            materialization.OpenCanonicalPngReadStream);
    }

    [Fact]
    public async Task PreCanceledExportLeavesPrimaryReady()
    {
        using var fixture = new ReplayFixture();
        var entry = await fixture.CommitAsync();
        var descriptor = await fixture.Repository.GetReplayPayloadAsync(
            entry.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        var primary = Assert.IsType<ClipboardAssetReference>(
            descriptor.Image?.PrimaryPng);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Coordinator.MaterializeForExportAsync(
                descriptor,
                cancellation.Token));

        var stored = await fixture.Repository.LoadAssetAsync(
            primary.Hash,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(ClipboardAssetState.Ready, stored.State);
        Assert.Equal(primary.Revision, stored.Revision);
    }

    private static byte[] DecodeBgra32(
        Stream stream,
        int expectedWidth,
        int expectedHeight)
    {
        using (stream)
        {
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = Assert.Single(decoder.Frames);
            Assert.Equal(expectedWidth, frame.PixelWidth);
            Assert.Equal(expectedHeight, frame.PixelHeight);
            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(
                    frame,
                    PixelFormats.Bgra32,
                    destinationPalette: null,
                    alphaThreshold: 0);
            var pixels = new byte[expectedWidth * expectedHeight * 4];
            source.CopyPixels(pixels, expectedWidth * 4, 0);
            return pixels;
        }
    }

    private static int ToPixelsPerMeter(int? dpiMilli) => dpiMilli is null
        ? 0
        : checked((int)Math.Round(
            dpiMilli.Value * (10d / 254d),
            MidpointRounding.AwayFromZero));

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private sealed class ReplayFixture : IDisposable
    {
        private readonly string _directory;
        private readonly ClipboardImageCaptureOptions _options = new();

        public ReplayFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "vok-image-replay-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Repository = new SqliteClipboardHistoryRepository(
                Path.Combine(_directory, "history.db"));
            var assetStore = new ContentAddressedClipboardAssetStore(
                Path.Combine(_directory, "assets"),
                _options);
            var replayTemporaryStore = new ClipboardImageTemporaryStore(
                Path.Combine(_directory, "replay"));
            ReplayTemporaryStore = replayTemporaryStore;
            Coordinator = new ClipboardImageAssetCoordinator(
                Repository,
                assetStore,
                _options,
                TimeSpan.FromSeconds(5),
                diagnosticFault: null,
                replayTemporaryStore: replayTemporaryStore);
        }

        public SqliteClipboardHistoryRepository Repository { get; }

        public ClipboardImageAssetCoordinator Coordinator { get; }

        public ClipboardImageTemporaryStore ReplayTemporaryStore { get; }

        public async Task<ClipboardEntry> CommitAsync()
        {
            byte[] pixels =
            [
                0x00, 0x00, 0xff, 0xff,
                0x00, 0xff, 0x00, 0x80,
                0xff, 0x00, 0x00, 0xff,
                0xff, 0xff, 0xff, 0x40,
            ];
            var source = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                palette: null,
                pixels,
                stride: 8);
            source.Freeze();
            var png = EncodePng(source);
            using var capture = new ClipboardCaptureResult(
                10,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Captured,
                [new ClipboardFormatSnapshot(
                    0,
                    ClipboardKnownFormats.PngName,
                    ClipboardRepresentationKind.Png,
                    ClipboardFormatRole.Content,
                    ClipboardFormatCaptureState.Captured,
                    png,
                    SourceByteLength: png.LongLength,
                    ImageHeader: new ClipboardImageHeader(2, 2, 16))]);
            using var normalizationTemporaryStore = new ClipboardImageTemporaryStore(
                Path.Combine(_directory, "normalize"));
            var result = new ClipboardImageNormalizer(_options).Normalize(
                capture,
                normalizationTemporaryStore,
                TestContext.Current.CancellationToken);
            return await Coordinator.CommitAsync(
                result,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Repository.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        private static byte[] EncodePng(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
    }
}
