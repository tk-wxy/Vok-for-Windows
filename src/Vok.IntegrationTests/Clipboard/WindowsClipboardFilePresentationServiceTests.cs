using System.IO;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardFilePresentationServiceTests
{
    [Fact]
    public async Task LocalExecutableIsAvailableAndUsesRequestedIconSize()
    {
        var executable = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executable));
        using var executor = new WindowsShellStaExecutor();
        var requestedSizes = new List<int>();
        using var service = new WindowsClipboardFilePresentationService(
            executor,
            (_, size) =>
            {
                requestedSizes.Add(size);
                return [0x89, 0x50, 0x4E, 0x47];
            });

        var result = await service.InspectAsync(
            [FileReference(executable)],
            48,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClipboardFileAvailability.Available, result.Availability);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], result.IconPngBytes);
        Assert.Equal([48], requestedSizes);
    }

    [Fact]
    public async Task MissingAndUncPathsDoNotEnterShellIconExtraction()
    {
        using var executor = new WindowsShellStaExecutor();
        var extractionCount = 0;
        using var service = new WindowsClipboardFilePresentationService(
            executor,
            (_, _) =>
            {
                extractionCount++;
                return [1];
            });
        var missing = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"),
            "missing.txt");

        var missingResult = await service.InspectAsync(
            [FileReference(missing)],
            48,
            TestContext.Current.CancellationToken);
        var remoteResult = await service.InspectAsync(
            [FileReference(@"\\vok-never-contact\offline\item.txt")],
            48,
            TestContext.Current.CancellationToken);

        Assert.Equal(ClipboardFileAvailability.Missing, missingResult.Availability);
        Assert.Null(missingResult.IconPngBytes);
        Assert.Equal(ClipboardFileAvailability.Unknown, remoteResult.Availability);
        Assert.Null(remoteResult.IconPngBytes);
        Assert.Equal(0, extractionCount);
    }

    [Fact]
    public async Task TransientIconFailureFallsBackAndIsNotCached()
    {
        var executable = Environment.ProcessPath!;
        using var executor = new WindowsShellStaExecutor();
        var requestedSizes = new List<int>();
        using var service = new WindowsClipboardFilePresentationService(
            executor,
            (_, size) =>
            {
                requestedSizes.Add(size);
                return requestedSizes.Count < 3 ? null : [7, 8, 9];
            });

        var first = await service.InspectAsync(
            [FileReference(executable)],
            48,
            TestContext.Current.CancellationToken);
        var second = await service.InspectAsync(
            [FileReference(executable)],
            48,
            TestContext.Current.CancellationToken);

        Assert.Null(first.IconPngBytes);
        Assert.Equal([7, 8, 9], second.IconPngBytes);
        Assert.Equal([48, 96, 48], requestedSizes);
    }

    [Fact]
    public async Task SharedAdaptersDoNotDisposeTheExecutorTheyBorrow()
    {
        using var executor = new WindowsShellStaExecutor();
        var clipboard = new WindowsClipboardFilePresentationService(executor);
        var launcher = new WindowsLauncherShellService(executor);
        clipboard.Dispose();

        var resolution = Assert.Single(await launcher.ResolveAsync(
            [Environment.ProcessPath!],
            TestContext.Current.CancellationToken));

        Assert.True(resolution.Succeeded);
        launcher.Dispose();
        Assert.Equal(
            42,
            await executor.ExecuteAsync(
                static () => 42,
                TestContext.Current.CancellationToken));
    }

    private static ClipboardFileReference FileReference(string path) => new(
        0,
        path,
        Path.GetFullPath(path),
        Path.GetFileName(path));
}
