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

    [Fact]
    public async Task ReorderPreviewsInMemoryAndPersistsExactlyOnceOnDrop()
    {
        var items = CreateItems(5);
        var repository = new ReorderRepository(items);
        using var viewModel = new LauncherViewModel(repository, new FakeShell([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var session = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[0]));

        Assert.True(viewModel.PreviewReorder(session, targetIndex: 3));
        Assert.Equal(
            ["Item 2", "Item 3", "Item 4", "Item 1", "Item 5"],
            viewModel.Items.Select(static item => item.DisplayName));

        Assert.True(await viewModel.CommitReorderAsync(
            session,
            TestContext.Current.CancellationToken));

        Assert.Equal(4, Assert.Single(repository.MoveSlots));
        Assert.False(viewModel.IsReordering);
        Assert.False(session.Item.IsDragging);
        Assert.Equal(
            [0L, 1L, 2L, 3L, 4L],
            viewModel.Items.Select(static item => item.Item.SortOrder));
        Assert.False(await viewModel.CommitReorderAsync(
            session,
            TestContext.Current.CancellationToken));
        Assert.Single(repository.MoveSlots);
    }

    [Fact]
    public async Task ReorderCancelAndPersistenceFailureRestoreTheExactSnapshot()
    {
        var items = CreateItems(5);
        var repository = new ReorderRepository(items);
        using var viewModel = new LauncherViewModel(repository, new FakeShell([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var canceled = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[4]));
        Assert.True(viewModel.PreviewReorder(canceled, targetIndex: 0));

        Assert.True(viewModel.CancelReorder(canceled));

        Assert.Equal(
            items.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Item.Id));
        Assert.Empty(repository.MoveSlots);

        var replacement = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[1]));
        Assert.False(viewModel.PreviewReorder(canceled, targetIndex: 3));
        Assert.True(viewModel.CancelReorder(replacement));

        repository.MoveSucceeds = false;
        var failed = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[0]));
        Assert.True(viewModel.PreviewReorder(failed, targetIndex: 2));

        Assert.False(await viewModel.CommitReorderAsync(
            failed,
            TestContext.Current.CancellationToken));

        Assert.Equal(
            items.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Item.Id));
        Assert.False(viewModel.IsReordering);
        Assert.Contains("保存启动台顺序失败", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchFeedbackCompletionAndWatchdogCanClaimEachGenerationOnlyOnce()
    {
        var gate = new LauncherLaunchFeedbackGate();
        var first = gate.Begin();

        Assert.True(gate.TryClaim(first));
        Assert.False(gate.TryClaim(first));

        var stale = gate.Begin();
        var current = gate.Begin();

        Assert.False(gate.TryClaim(stale));
        Assert.True(gate.TryClaim(current));
        Assert.False(gate.HasActive);

        var canceled = gate.Begin();
        gate.Cancel();

        Assert.False(gate.TryClaim(canceled));
    }

    [Fact]
    public async Task CanceledReorderCommitRollsBackWithoutLeavingTheCatalogLocked()
    {
        var items = CreateItems(4);
        var repository = new ReorderRepository(items);
        using var viewModel = new LauncherViewModel(repository, new FakeShell([]));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var session = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[0]));
        Assert.True(viewModel.PreviewReorder(session, targetIndex: 2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.CommitReorderAsync(session, cancellation.Token));

        Assert.False(viewModel.IsReordering);
        Assert.False(viewModel.IsCatalogBusy);
        Assert.Equal(
            items.Select(static item => item.Id),
            viewModel.Items.Select(static item => item.Item.Id));
        Assert.Empty(repository.MoveSlots);

        var next = Assert.IsType<LauncherReorderSession>(
            viewModel.BeginReorder(viewModel.Items[1]));
        Assert.True(viewModel.CancelReorder(next));
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

    private static IReadOnlyList<LauncherItem> CreateItems(int count)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, count)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherItemKind.File,
                $@"C:\Apps\Item-{index + 1}.exe",
                $"0:C:\\Apps\\Item-{index + 1}.exe",
                $"Item {index + 1}",
                null,
                index,
                1,
                now,
                now))
            .ToArray();
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

    private sealed class ReorderRepository(IReadOnlyList<LauncherItem> items)
        : ILauncherRepository
    {
        public List<int> MoveSlots { get; } = [];

        public bool MoveSucceeds { get; set; } = true;

        public Task<LauncherPage> LoadPageAsync(
            long? afterSortOrder,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var page = items
                .Where(item => afterSortOrder is null || item.SortOrder > afterSortOrder)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult(new LauncherPage(page, null, items.Count));
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
            CancellationToken cancellationToken = default)
        {
            MoveSlots.Add(slot);
            return Task.FromResult(MoveSucceeds);
        }
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
