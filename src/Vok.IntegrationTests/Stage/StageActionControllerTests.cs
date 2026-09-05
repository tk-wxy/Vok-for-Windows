using Vok.Core.Platform;
using Vok.Core.Stage;
using Vok.Core.Workspace;
using Vok.Features.Stage;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageActionControllerTests
{
    [Fact]
    public async Task OpenRunsShellOnlyAfterStageManagementHideCompletes()
    {
        var log = new List<string>();
        var item = CreateItem();
        var files = new FakeFiles(log);
        using var viewModel = new StageViewModel(
            new StaticRepository(item),
            files);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new StageActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: true),
            files,
            new FakeClipboard(log));

        await viewModel.OpenCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(["workspace.hide.stage", "shell.open"], log);
        Assert.Contains("交给 Windows 打开", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedStageManagementHideProducesNoShellSideEffect()
    {
        var log = new List<string>();
        var files = new FakeFiles(log);
        using var viewModel = new StageViewModel(
            new StaticRepository(CreateItem()),
            files);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new StageActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: false),
            files,
            new FakeClipboard(log));

        await viewModel.RevealCommand.ExecuteAsync(viewModel.Items[0]);

        Assert.Equal(["workspace.hide.stage"], log);
        Assert.Contains("已取消中转站文件操作", viewModel.StatusText, StringComparison.Ordinal);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task MissingItemDoesNotStartAHideOrShellAction()
    {
        var log = new List<string>();
        var files = new FakeFiles(log);
        using var viewModel = new StageViewModel(
            new StaticRepository(CreateItem()),
            files);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new StageActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: true),
            files,
            new FakeClipboard(log));
        var projection = viewModel.Items[0];
        Assert.True(projection.ApplyAvailability(
            projection.Revision,
            StageFileAvailability.Missing));

        await viewModel.OpenCommand.ExecuteAsync(projection);
        await viewModel.RevealCommand.ExecuteAsync(projection);

        Assert.Empty(log);
        Assert.False(projection.CanOpenOrReveal);
    }

    [Fact]
    public async Task CopyPathKeepsTheWorkspaceVisibleAndDoesNotMutateTheItem()
    {
        var log = new List<string>();
        var files = new FakeFiles(log);
        var clipboard = new FakeClipboard(log);
        using var viewModel = new StageViewModel(
            new StaticRepository(CreateItem()),
            files);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        using var controller = new StageActionController(
            viewModel,
            new FakeWorkspace(log, hideSucceeds: true),
            files,
            clipboard);
        var projection = viewModel.Items[0];
        var before = projection.Item;
        Assert.True(projection.ApplyAvailability(
            projection.Revision,
            StageFileAvailability.Missing));

        await viewModel.CopyPathCommand.ExecuteAsync(projection);

        Assert.Equal(["clipboard.copy"], log);
        Assert.Equal(before.TargetValue, Assert.Single(clipboard.Paths));
        Assert.Equal(before, projection.Item);
        Assert.Contains("已复制原路径", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
    }

    private static StageItem CreateItem()
    {
        var now = DateTimeOffset.UtcNow;
        return new StageItem(
            Guid.NewGuid(),
            StageItemKind.ShellLink,
            @"C:\Stage\Target.lnk",
            @"C:\STAGE\TARGET.LNK",
            "Target",
            1,
            false,
            1,
            now,
            now);
    }

    private sealed class StaticRepository(StageItem item) : IStageRepository
    {
        public Task<StagePage> LoadPageAsync(
            StagePageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StagePage([item], null, 1));

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRemoveResult> RemoveAsync(
            Guid itemId,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageItemMutationResult> SetPinnedAsync(
            Guid itemId,
            long expectedRevision,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFiles(List<string> log) : IStageFileService
    {
        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StageTargetResolution>>([]);

        public Task<StageFileAvailability> InspectAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StageFileAvailability.Available);

        public Task OpenAsync(
            StageItem item,
            CancellationToken cancellationToken = default)
        {
            log.Add("shell.open");
            return Task.CompletedTask;
        }

        public Task RevealInExplorerAsync(
            StageItem item,
            CancellationToken cancellationToken = default)
        {
            log.Add("shell.reveal");
            return Task.CompletedTask;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> HideForStageManagementActionAsync(
            CancellationToken cancellationToken = default)
        {
            log.Add("workspace.hide.stage");
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

    private sealed class FakeClipboard(List<string> log) : IStageClipboardService
    {
        public List<string> Paths { get; } = [];

        public Task CopyPathTextAsync(
            string targetValue,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(targetValue);
            log.Add("clipboard.copy");
            return Task.CompletedTask;
        }
    }
}
