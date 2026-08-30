using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ContentAddressedClipboardAssetStoreTests
{
    [Fact]
    public void CommitFlushesVerifiesAndReusesExactContentAddressedPng()
    {
        var directory = CreateTestDirectory();
        try
        {
            var png = EncodePng(2, 2, [
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 128,
            ]);
            using var store = new ContentAddressedClipboardAssetStore(directory);

            var first = store.CommitPng(
                new MemoryStream(png, writable: false),
                png.Length,
                2,
                2,
                TestContext.Current.CancellationToken);
            var second = store.CommitPng(
                new MemoryStream(png, writable: false),
                png.Length,
                2,
                2,
                TestContext.Current.CancellationToken);

            Assert.Equal(first, second);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
                first.Hash);
            Assert.Equal($"{first.Hash[..2]}/{first.Hash}.png", first.RelativePath);
            Assert.Equal("image/png", first.MediaType);
            Assert.Equal(png.Length, first.ByteLength);
            Assert.Equal(
                png,
                File.ReadAllBytes(GetAssetPath(directory, first.Hash)));
            Assert.Equal(0, store.OwnedStagingFileCount);
            store.ValidatePng(first, TestContext.Current.CancellationToken);
            Assert.Equal(
                png,
                store.ReadPng(first, TestContext.Current.CancellationToken));
            using (var lease = store.OpenVerifiedPng(
                       first,
                       TestContext.Current.CancellationToken))
            {
                Assert.Equal(png, ReadAll(lease.OpenRead()));
                Assert.Equal(png, ReadAll(lease.OpenRead()));
                Assert.Throws<IOException>(() =>
                    File.Delete(GetAssetPath(directory, first.Hash)));
            }

            Assert.True(File.Exists(GetAssetPath(directory, first.Hash)));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void ExistingHashPathWithUnknownBytesIsNeverOverwritten()
    {
        var directory = CreateTestDirectory();
        try
        {
            var png = EncodePng(1, 1, [10, 20, 30, 255]);
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var path = GetAssetPath(directory, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var unknown = Enumerable.Repeat((byte)0x5a, png.Length).ToArray();
            File.WriteAllBytes(path, unknown);
            using var store = new ContentAddressedClipboardAssetStore(directory);

            Assert.Throws<InvalidDataException>(() => store.CommitPng(
                new MemoryStream(png, writable: false),
                png.Length,
                1,
                1,
                TestContext.Current.CancellationToken));

            Assert.Equal(unknown, File.ReadAllBytes(path));
            Assert.Equal(0, store.OwnedStagingFileCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void FailedOrCancelledCommitLeavesNoOwnedStagingFile()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var store = new ContentAddressedClipboardAssetStore(directory);
            var truncated = EncodePng(1, 1, [1, 2, 3, 255]);
            Assert.Throws<InvalidDataException>(() => store.CommitPng(
                new MemoryStream(truncated, 0, truncated.Length - 1, writable: false),
                truncated.Length,
                1,
                1,
                TestContext.Current.CancellationToken));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => store.CommitPng(
                new MemoryStream(truncated, writable: false),
                truncated.Length,
                1,
                1,
                cancelled.Token));
            Assert.Equal(0, store.OwnedStagingFileCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void StartupCleanupOwnsOnlyAssetTemporaryPattern()
    {
        var directory = CreateTestDirectory();
        try
        {
            var staging = Path.Combine(directory, ".staging");
            Directory.CreateDirectory(staging);
            var owned = Path.Combine(staging, $"asset-{Guid.NewGuid():N}.tmp");
            var capture = Path.Combine(staging, $"capture-{Guid.NewGuid():N}.blob");
            var normalized = Path.Combine(staging, $"normalized-{Guid.NewGuid():N}.png");
            var arbitrary = Path.Combine(staging, "keep.tmp");
            File.WriteAllBytes(owned, [1]);
            File.WriteAllBytes(capture, [2]);
            File.WriteAllBytes(normalized, [3]);
            File.WriteAllBytes(arbitrary, [4]);

            using var store = new ContentAddressedClipboardAssetStore(directory);

            Assert.False(File.Exists(owned));
            Assert.True(File.Exists(capture));
            Assert.True(File.Exists(normalized));
            Assert.True(File.Exists(arbitrary));
        }
        finally
        {
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

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private static string GetAssetPath(string root, string hash) =>
        Path.Combine(root, hash[..2], $"{hash}.png");

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
