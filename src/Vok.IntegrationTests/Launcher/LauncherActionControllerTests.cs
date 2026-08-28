using Vok.Core.Launcher;
using Vok.Core.Workspace;
using Vok.Features.Launcher;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Launcher;

public sealed class LauncherActionControllerTests
{
    [Fact]
    public async Task OpenRunsShellOnlyAfterWorkspaceHideTransactionCompletes()
    {
        var log = new List<string>();
        var item = CreateItem();
        var shell = new FakeShell(log);
        using var viewModel = new LauncherViewModel(new FakeRepository(item), shell);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new LauncherActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: true),
            shell);

        await viewModel.OpenCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(["workspace.hide", "shell.open"], log);
    }

    [Fact]
    public async Task FailedWorkspaceHideCancelsShellAction()
    {
        var log = new List<string>();
        var item = CreateItem();
        var shell = new FakeShell(log);
        using var viewModel = new LauncherViewModel(new FakeRepository(item), shell);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new LauncherActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: false),
            shell);

        await viewModel.OpenCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(["workspace.hide"], log);
        Assert.Contains("已取消启动操作", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogLoadsMetadataByPageWhileKeepingTotalCount()
    {
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, 300)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherItemKind.File,
                $"C:\\Tools\\App-{index}.exe",
                $"0:C:\\Tools\\App-{index}.exe",
                $"App {index}",
                null,
                index,
                1,
                now,
                now))
            .ToArray();
        using var viewModel = new LauncherViewModel(
            new PagingRepository(items),
            new FakeShell([]));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(300, viewModel.Count);
        Assert.Equal(256, viewModel.Items.Count);
        Assert.Equal(64, viewModel.Rows.Count);

        await viewModel.LoadNextPageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(300, viewModel.Items.Count);
        Assert.Equal(75, viewModel.Rows.Count);
    }

    private static LauncherItem CreateItem()
    {
        var now = DateTimeOffset.UtcNow;
        return new LauncherItem(
            Guid.NewGuid(),
            LauncherItemKind.File,
            "C:\\Tools\\Alpha.exe",
            "0:C:\\Tools\\Alpha.exe",
            "Alpha",
            null,
            0,
            1,
            now,
            now);
    }

    private sealed class FakeRepository(LauncherItem item) : ILauncherRepository
    {
        public Task<LauncherPage> LoadPageAsync(
            long? afterSortOrder,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(afterSortOrder is null
                ? new LauncherPage([item], null, 1)
                : new LauncherPage([], null, 1));

        public Task<LauncherAddResult> AddAsync(
            IReadOnlyList<LauncherTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherAddResult([], []));

        public Task<bool> RemoveAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> MoveToSlotAsync(
            Guid itemId,
            int slot,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class PagingRepository(IReadOnlyList<LauncherItem> items)
        : ILauncherRepository
    {
        public Task<LauncherPage> LoadPageAsync(
            long? afterSortOrder,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var page = items
                .Where(item => afterSortOrder is null || item.SortOrder > afterSortOrder)
                .Take(pageSize)
                .ToArray();
            long? next = page.Length == pageSize ? page[^1].SortOrder : null;
            return Task.FromResult(new LauncherPage(page, next, items.Count));
        }

        public Task<LauncherAddResult> AddAsync(
            IReadOnlyList<LauncherTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherAddResult([], []));

        public Task<bool> RemoveAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> MoveToSlotAsync(
            Guid itemId,
            int slot,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeShell(List<string> log) : ILauncherShellService
    {
        public Task<IReadOnlyList<LauncherTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LauncherTargetResolution>>([]);

        public Task<LauncherIconData?> GetIconAsync(
            LauncherItem item,
            int pixelSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LauncherIconData?>(null);

        public Task OpenAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default)
        {
            log.Add("shell.open");
            return Task.CompletedTask;
        }

        public Task RevealInExplorerAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default)
        {
            log.Add("shell.reveal");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeWorkspace(List<string> log, bool hideSucceeds)
        : IWorkspaceCommands
    {
        public event EventHandler? ShutdownRequested
        {
            add { }
            remove { }
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task<bool> HideForLauncherActionAsync(
            CancellationToken cancellationToken = default)
        {
            log.Add("workspace.hide");
            return Task.FromResult(hideSucceeds);
        }

        public void EnsureVisible(
            ForegroundTarget foregroundTarget,
            long requestedTimestamp,
            WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent)
        {
        }

        public void RequestExit()
        {
        }

        public void ReportFault(Exception exception)
        {
        }
    }
}
