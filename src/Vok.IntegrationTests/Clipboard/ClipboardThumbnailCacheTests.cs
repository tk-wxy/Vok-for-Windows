using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

[Collection(WpfApplicationCollection.Name)]
public sealed class ClipboardThumbnailCacheTests(WpfApplicationFixture wpf)
{
    [Fact]
    public async Task CacheChargesDecodedBytesEvictsLruAndReusesHash()
    {
        var hashes = new[] { Hash('a'), Hash('b'), Hash('c') };
        var png = CreatePng(1, 1, [1, 2, 3, 255]);
        var service = new FakeImageAssetService(png);
        ClipboardThumbnailCache? cache = null;
        wpf.Invoke(() => cache = new ClipboardThumbnailCache(
            wpf.Application.Dispatcher,
            service,
            new ClipboardImageCaptureOptions(thumbnailCacheBytes: 8)));
        try
        {
            var first = await LoadAsync(cache!, hashes[0], 1);
            var second = await LoadAsync(cache!, hashes[1], 1);
            var third = await LoadAsync(cache!, hashes[2], 1);

            wpf.Invoke(() =>
            {
                Assert.True(first.IsReady);
                Assert.True(second.IsReady);
                Assert.True(third.IsReady);
                Assert.True(first.Image!.IsFrozen);
                Assert.Equal(8, cache!.CachedDecodedBytes);
                Assert.Equal(2, cache.Count);
                Assert.Equal(3, service.LoadCount);
            });

            _ = await LoadAsync(cache!, hashes[1], 99);
            wpf.Invoke(() => Assert.Equal(3, service.LoadCount));
            _ = await LoadAsync(cache!, hashes[0], 1);
            wpf.Invoke(() =>
            {
                Assert.Equal(4, service.LoadCount);
                Assert.Equal(8, cache!.CachedDecodedBytes);
                Assert.Equal(2, cache.Count);
            });
        }
        finally
        {
            wpf.Invoke(() => cache?.Dispose());
            service.Dispose();
        }
    }

    [Fact]
    public async Task DisposingCacheCancelsActiveLoadWithoutPublishingAnEntry()
    {
        var service = new BlockingImageAssetService();
        ClipboardThumbnailCache? cache = null;
        Task<ClipboardThumbnailPresentation>? load = null;
        wpf.Invoke(() =>
        {
            cache = new ClipboardThumbnailCache(
                wpf.Application.Dispatcher,
                service);
            load = cache.LoadAsync(Hash('d'), 1);
        });
        await service.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        wpf.Invoke(cache!.Dispose);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await load!);
        Assert.True(service.CancellationObserved);
        wpf.Invoke(() =>
        {
            Assert.Equal(0, cache!.CachedDecodedBytes);
            Assert.Equal(0, cache.Count);
        });
        service.Dispose();
    }

    [Fact]
    public async Task DecodeFailureNeverEntersCache()
    {
        var service = new FakeImageAssetService([0x00, 0x01, 0x02]);
        ClipboardThumbnailCache? cache = null;
        wpf.Invoke(() => cache = new ClipboardThumbnailCache(
            wpf.Application.Dispatcher,
            service));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await LoadAsync(cache!, Hash('e'), 1));
            wpf.Invoke(() =>
            {
                Assert.Equal(0, cache!.CachedDecodedBytes);
                Assert.Equal(0, cache.Count);
            });
        }
        finally
        {
            wpf.Invoke(() => cache?.Dispose());
            service.Dispose();
        }
    }

    private async Task<ClipboardThumbnailPresentation> LoadAsync(
        ClipboardThumbnailCache cache,
        string hash,
        long revision)
    {
        Task<ClipboardThumbnailPresentation>? task = null;
        wpf.Invoke(() => task = cache.LoadAsync(
            hash,
            revision,
            TestContext.Current.CancellationToken));
        return await task!;
    }

    private byte[] CreatePng(int width, int height, byte[] bgraPixels)
    {
        byte[]? result = null;
        wpf.Invoke(() =>
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
            result = output.ToArray();
        });
        return result!;
    }

    private static string Hash(char character) => new(character, 64);

    private sealed class FakeImageAssetService(byte[] pngBytes)
        : IClipboardImageAssetService
    {
        public int LoadCount { get; private set; }

        public Task<ClipboardImageThumbnailResult?> LoadThumbnailAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult<ClipboardImageThumbnailResult?>(new(
                assetHash,
                ClipboardAssetState.Ready,
                expectedRevision,
                1,
                1,
                pngBytes.ToArray()));
        }

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Dispose()
        {
        }
    }

    private sealed class BlockingImageAssetService : IClipboardImageAssetService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<ClipboardImageThumbnailResult?> LoadThumbnailAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Dispose()
        {
        }
    }
}
