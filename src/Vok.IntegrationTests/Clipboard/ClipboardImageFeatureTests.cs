using System.IO;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

[Collection(WpfApplicationCollection.Name)]
public sealed class ClipboardImageFeatureTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void EnabledFeatureSharesOneCoordinatorAndStopsOwnedWorkers()
    {
        var directory = CreateTestDirectory();
        WindowsClipboardImageExecutor? normalizer = null;
        ClipboardImageFeature? feature = null;
        var repository = new SqliteClipboardHistoryRepository(
            Path.Combine(directory, "vok.db"));
        try
        {
            var options = new ClipboardImageCaptureOptions();
            wpf.Invoke(() =>
            {
                feature = ClipboardImageFeature.Create(
                    repository,
                    wpf.Application.Dispatcher,
                    options,
                    normalizerFactory: () =>
                    {
                        normalizer = new WindowsClipboardImageExecutor(
                            options,
                            new ClipboardImageNormalizer(options),
                            new ClipboardImageTemporaryStore(
                                Path.Combine(directory, "normalization")),
                            TimeSpan.FromSeconds(5));
                        return normalizer;
                    },
                    assetStoreFactory: () =>
                        new ContentAddressedClipboardAssetStore(
                            Path.Combine(directory, "assets"),
                            options));
            });

            Assert.NotNull(normalizer);
            Assert.NotNull(feature);
            Assert.True(feature.IsEnabled);
            Assert.NotNull(feature.CaptureService);
            Assert.Null(feature.UnavailableStatus);
            Assert.NotNull(feature.ThumbnailCache);
            Assert.Same(feature.AssetService, feature.ReplayMaterializer);
            var assets = Assert.IsType<ClipboardImageAssetCoordinator>(
                feature.AssetService);
            Assert.True(normalizer.IsWorkerThreadAlive);
            Assert.True(assets.IsWorkerThreadAlive);

            wpf.Invoke(feature.Dispose);

            Assert.False(normalizer.IsWorkerThreadAlive);
            Assert.False(assets.IsWorkerThreadAlive);
            Assert.Equal(0, normalizer.OutstandingJobCount);
            Assert.Equal(0, normalizer.OutstandingTemporaryLeaseCount);
            Assert.Equal(0, assets.OutstandingJobCount);
            Assert.Equal(0, assets.OutstandingReplayLeaseCount);
        }
        finally
        {
            wpf.Invoke(() => feature?.Dispose());
            repository.Dispose();
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void AssetStoreStartupFailureDisablesOnlyImageFeatureAndStopsPartialWorker()
    {
        var directory = CreateTestDirectory();
        WindowsClipboardImageExecutor? normalizer = null;
        ClipboardImageFeature? feature = null;
        var repository = new SqliteClipboardHistoryRepository(
            Path.Combine(directory, "vok.db"));
        try
        {
            var options = new ClipboardImageCaptureOptions();
            wpf.Invoke(() =>
            {
                feature = ClipboardImageFeature.Create(
                    repository,
                    wpf.Application.Dispatcher,
                    options,
                    normalizerFactory: () =>
                    {
                        normalizer = new WindowsClipboardImageExecutor(
                            options,
                            new ClipboardImageNormalizer(options),
                            new ClipboardImageTemporaryStore(
                                Path.Combine(directory, "normalization")),
                            TimeSpan.FromSeconds(5));
                        return normalizer;
                    },
                    assetStoreFactory: static () =>
                        throw new IOException("deliberate asset root failure"));
            });

            Assert.NotNull(normalizer);
            Assert.NotNull(feature);
            Assert.False(feature.IsEnabled);
            Assert.Null(feature.CaptureService);
            Assert.NotNull(feature.UnavailableStatus);
            Assert.Null(feature.AssetService);
            Assert.Null(feature.ReplayMaterializer);
            Assert.Null(feature.ThumbnailCache);
            Assert.False(normalizer.IsWorkerThreadAlive);
            Assert.Equal(0, normalizer.OutstandingJobCount);
            Assert.Equal(0, normalizer.OutstandingTemporaryLeaseCount);
        }
        finally
        {
            wpf.Invoke(() => feature?.Dispose());
            repository.Dispose();
            DeleteTestDirectory(directory);
        }
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
}
