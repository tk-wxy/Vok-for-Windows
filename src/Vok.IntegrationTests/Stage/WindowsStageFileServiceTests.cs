using System.IO;
using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Stage;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class WindowsStageFileServiceTests
{
    [Fact]
    public async Task ResolvesFilesDirectoriesAndLinksWithoutDereferencingTheLink()
    {
        var directory = CreateTestDirectory();
        var file = Path.Combine(directory, "Ä-document.txt");
        var link = Path.Combine(directory, "Sample App.lnk");
        await File.WriteAllTextAsync(
            file,
            "content",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            link,
            [],
            TestContext.Current.CancellationToken);
        try
        {
            using var service = new WindowsStageFileService();
            var results = await service.ResolveAsync(
                [file, directory + Path.DirectorySeparatorChar, link],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageItemKind.File, results[0].Target?.Kind);
            Assert.Equal(StageItemKind.Directory, results[1].Target?.Kind);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                results[1].Target?.TargetValue);
            Assert.Equal(StageItemKind.ShellLink, results[2].Target?.Kind);
            Assert.Equal(Path.GetFullPath(link), results[2].Target?.TargetValue);
            Assert.Equal("Sample App", results[2].Target?.ResolvedName);
            Assert.Equal(
                Path.GetFullPath(file).ToUpperInvariant(),
                results[0].Target?.CanonicalKey);
            Assert.All(results, result =>
            {
                Assert.True(result.Succeeded);
                Assert.Null(result.Failure);
                Assert.Null(result.Error);
            });
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidInputsFailIndividuallyWithoutSuppressingValidTargets()
    {
        var directory = CreateTestDirectory();
        var file = Path.Combine(directory, "valid.txt");
        await File.WriteAllTextAsync(
            file,
            "content",
            TestContext.Current.CancellationToken);
        try
        {
            using var service = new WindowsStageFileService();
            var results = await service.ResolveAsync(
                ["", "relative.txt", Path.Combine(directory, "missing.txt"), file],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageTargetFailureKind.Empty, results[0].Failure);
            Assert.Equal(StageTargetFailureKind.NotFullyQualified, results[1].Failure);
            Assert.Equal(StageTargetFailureKind.Missing, results[2].Failure);
            Assert.True(results[3].Succeeded);
            Assert.All(results.Take(3), result =>
            {
                Assert.False(result.Succeeded);
                Assert.Null(result.Target);
                Assert.False(string.IsNullOrWhiteSpace(result.Error));
            });
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationBeforeQueueClaimDoesNotResolveInputs()
    {
        using var service = new WindowsStageFileService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveAsync(
                [Environment.ProcessPath!],
                cancellation.Token));
    }

    [Fact]
    public async Task InspectionDistinguishesLocalMissingAndNetworkUnknown()
    {
        var directory = CreateTestDirectory();
        var file = Path.Combine(directory, "available.txt");
        await File.WriteAllTextAsync(
            file,
            "content",
            TestContext.Current.CancellationToken);
        try
        {
            using var service = new WindowsStageFileService();

            Assert.Equal(
                StageFileAvailability.Available,
                await service.InspectAsync(
                    Item(file),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                StageFileAvailability.Missing,
                await service.InspectAsync(
                    Item(Path.Combine(directory, "missing.txt")),
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                StageFileAvailability.Unknown,
                await service.InspectAsync(
                    Item(@"\\server.invalid\share\missing.txt"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationBeforeQueueClaimDoesNotInspectTarget()
    {
        using var service = new WindowsStageFileService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.InspectAsync(
                Item(Environment.ProcessPath!),
                cancellation.Token));
    }

    private static StageItem Item(string path)
    {
        var now = DateTimeOffset.UtcNow;
        return new StageItem(
            Guid.NewGuid(),
            StageItemKind.File,
            path,
            path.ToUpperInvariant(),
            Path.GetFileName(path),
            1,
            false,
            1,
            now,
            now);
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
