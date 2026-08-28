using System.IO;
using Vok.Core.Launcher;
using Vok.Infrastructure.Windows.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Launcher;

public sealed class WindowsLauncherShellServiceTests
{
    [Fact]
    public async Task ResolvesFilesDirectoriesAndLinksWithoutDereferencingTheLink()
    {
        var directory = CreateTestDirectory();
        var file = Path.Combine(directory, "document.txt");
        var link = Path.Combine(directory, "Sample App.lnk");
        await File.WriteAllTextAsync(file, "content", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(link, [], TestContext.Current.CancellationToken);
        try
        {
            using var shell = new WindowsLauncherShellService();
            var results = await shell.ResolveAsync(
                [file, directory, link, Path.Combine(directory, "missing.exe")],
                TestContext.Current.CancellationToken);

            Assert.Equal(LauncherItemKind.File, results[0].Target?.Kind);
            Assert.Equal(LauncherItemKind.Directory, results[1].Target?.Kind);
            Assert.Equal(LauncherItemKind.ShellLink, results[2].Target?.Kind);
            Assert.Equal(Path.GetFullPath(link), results[2].Target?.TargetValue);
            Assert.Equal("Sample App", results[2].Target?.ResolvedName);
            Assert.False(results[3].Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(results[3].Error));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractsShellIconAsPngForAWindowsExecutable()
    {
        var executable = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executable));
        using var shell = new WindowsLauncherShellService();
        var resolution = Assert.Single(await shell.ResolveAsync(
            [executable],
            TestContext.Current.CancellationToken));
        var target = Assert.IsType<LauncherTargetDescriptor>(resolution.Target);
        var now = DateTimeOffset.UtcNow;
        var item = new LauncherItem(
            Guid.NewGuid(),
            target.Kind,
            target.TargetValue,
            target.CanonicalKey,
            target.ResolvedName,
            null,
            0,
            1,
            now,
            now);

        var icon = await shell.GetIconAsync(
            item,
            96,
            TestContext.Current.CancellationToken);

        Assert.NotNull(icon);
        Assert.Equal(96, icon.PixelSize);
        Assert.True(icon.PngBytes.Length > 8);
        Assert.Equal(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            icon.PngBytes[..8]);
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
