using System.IO;
using System.Security.Cryptography;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardImageDestinationServiceTests
{
    [Fact]
    public async Task VerifiedCanonicalPngUsesShellCollisionRenameAndPerItemResult()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new WindowsShellStaExecutor();
        using var service = new WindowsClipboardImageDestinationService(executor);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4];
        using var materialization = CreateMaterialization(png);
        var firstAuthorization = new Authorization(allow: true);
        var secondAuthorization = new Authorization(allow: true);

        var first = await service.CreateInDirectoryForTestingAsync(
            materialization,
            directory.Path,
            "Vok 图片 2026-08-31 123456.png",
            firstAuthorization,
            TestContext.Current.CancellationToken);
        var second = await service.CreateInDirectoryForTestingAsync(
            materialization,
            directory.Path,
            "Vok 图片 2026-08-31 123456.png",
            secondAuthorization,
            TestContext.Current.CancellationToken);

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.Equal("Vok 图片 2026-08-31 123456.png", first.CreatedFileName);
        Assert.NotEqual(first.CreatedFileName, second.CreatedFileName);
        Assert.EndsWith(".png", second.CreatedFileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(first.TemporaryFilesCleaned);
        Assert.True(second.TemporaryFilesCleaned);
        Assert.Equal(1, firstAuthorization.CallCount);
        Assert.Equal(1, secondAuthorization.CallCount);
        var files = Directory.GetFiles(directory.Path);
        Assert.Equal(2, files.Length);
        Assert.All(files, path => Assert.Equal(png, File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task RejectedAuthorizationLeavesDestinationUntouched()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new WindowsShellStaExecutor();
        using var service = new WindowsClipboardImageDestinationService(executor);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 5, 6, 7, 8];
        using var materialization = CreateMaterialization(png);
        var authorization = new Authorization(allow: false);

        var result = await service.CreateInDirectoryForTestingAsync(
            materialization,
            directory.Path,
            "Vok 图片 2026-08-31 123456.png",
            authorization,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ClipboardImageFileCreationStatus.StaleTarget,
            result.Status);
        Assert.True(result.TemporaryFilesCleaned);
        Assert.Equal(1, authorization.CallCount);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    private static ClipboardImageExportMaterialization CreateMaterialization(
        byte[] png)
    {
        var assetHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        return new ClipboardImageExportMaterialization(
            assetHash,
            new string('a', 64),
            1,
            1,
            hasAlpha: false,
            new MemoryLease(png));
    }

    private sealed class Authorization(bool allow)
        : IClipboardImageFileCreationAuthorization
    {
        public int CallCount { get; private set; }

        public bool TryClaimFileCreation()
        {
            CallCount++;
            return allow;
        }
    }

    private sealed class MemoryLease(byte[] bytes) : IClipboardPayloadLease
    {
        private int _disposed;

        public long Length => bytes.LongLength;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vok-image-destination-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
