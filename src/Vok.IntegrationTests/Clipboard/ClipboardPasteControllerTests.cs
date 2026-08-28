using Vok.Core.Clipboard;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Features.Clipboard;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardPasteControllerTests
{
    [Fact]
    public async Task SuccessfulPasteHonorsWriteHideVerifyInjectOrder()
    {
        var log = new List<string>();
        var entry = Entry();
        var repository = new FakeRepository(entry, log);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, CanInjectHide());
        var input = new FakeInput(log, succeeds: true);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            input);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "write", "hide", "verify", "inject"], log);
        Assert.Equal("已粘贴到呼出前目标。", viewModel.StatusText);
        Assert.Equal(1, input.CallCount);
    }

    [Fact]
    public async Task RestoreFailureStopsBeforeReceiptVerificationAndInjection()
    {
        var log = new List<string>();
        var entry = Entry();
        var repository = new FakeRepository(entry, log);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, WorkspaceActionHideResult.NotHidden);
        var input = new FakeInput(log, succeeds: true);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            input);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "write", "hide"], log);
        Assert.Contains("手动粘贴", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, input.CallCount);
    }

    [Fact]
    public async Task ExternalClipboardOverwriteStopsBeforeInputInjection()
    {
        var log = new List<string>();
        var entry = Entry();
        var repository = new FakeRepository(entry, log);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log) { IsCurrent = false };
        var input = new FakeInput(log, succeeds: true);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            input);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "write", "hide", "verify"], log);
        Assert.Contains("其他程序更新", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, input.CallCount);
    }

    [Fact]
    public async Task InputTargetChangeDegradesToCopiedOnly()
    {
        var log = new List<string>();
        var entry = Entry();
        var repository = new FakeRepository(entry, log);
        var viewModel = new ClipboardViewModel(repository);
        var input = new FakeInput(log, succeeds: false);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            new FakePlatform(log),
            new FakeWorkspace(log, CanInjectHide()),
            input);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "write", "hide", "verify", "inject"], log);
        Assert.Contains("焦点发生变化", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedInvocationWhileWriteIsActiveDoesNotWriteTwice()
    {
        var log = new List<string>();
        var entry = Entry();
        var repository = new FakeRepository(entry, log);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log) { HoldWrite = true };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true));
        var item = new ClipboardItemViewModel(entry);

        var first = viewModel.PasteCommand.ExecuteAsync(item);
        await platform.WriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await viewModel.PasteCommand.ExecuteAsync(item);
        platform.ReleaseWrite.TrySetResult();
        await first;

        Assert.Equal(1, platform.WriteCount);
        Assert.Equal(1, log.Count(static value => value == "materialize"));
    }

    private static ClipboardEntry Entry() => new(
        Guid.NewGuid(),
        ClipboardContentKind.Text,
        new string('b', 64),
        "hello",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        1,
        1,
        false,
        1);

    private static ClipboardReplayPayload Payload(ClipboardEntry entry) => new(
        ClipboardContentKind.Text,
        entry.SemanticHash,
        [new ClipboardStoredRepresentation(
            ClipboardRepresentationKind.UnicodeText,
            100,
            entry.PlainPreview,
            12)]);

    private static WorkspaceActionHideResult CanInjectHide()
    {
        var target = new ForegroundTarget(123, 10, 11, 124, 10, 11);
        return new WorkspaceActionHideResult(
            true,
            target,
            new ForegroundRestoreResult(
                ForegroundRestoreStatus.Confirmed,
                target.WindowHandle,
                target.FocusedWindowHandle));
    }

    private sealed class FakeRepository(ClipboardEntry entry, List<string> log)
        : IClipboardHistoryRepository
    {
        public Task<ClipboardPage> LoadPageAsync(
            ClipboardPageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardPage([], null, 0));

        public Task<ClipboardEntry> UpsertTextAsync(
            ClipboardTextContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(entry);

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            log.Add("materialize");
            return Task.FromResult<ClipboardReplayPayload?>(Payload(entry));
        }

        public Task<ClipboardEntry?> SetPinnedAsync(
            Guid entryId,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardEntry?>(entry with { IsPinned = isPinned });

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakePlatform(List<string> log) : IClipboardPlatformService
    {
        public bool IsCurrent { get; init; } = true;

        public bool HoldWrite { get; init; }

        public int WriteCount { get; private set; }

        public TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseWrite { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ClipboardCaptureResult> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task<ClipboardWriteReceipt> WriteAsync(
            ClipboardReplayPayload payload,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            log.Add("write");
            WriteCount++;
            WriteStarted.TrySetResult();
            if (HoldWrite)
            {
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            return new ClipboardWriteReceipt(10, operationId, payload.SemanticHash);
        }

        public Task<bool> IsCurrentAsync(
            ClipboardWriteReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            log.Add("verify");
            return Task.FromResult(IsCurrent);
        }

        public ClipboardDiagnosticSnapshot GetDiagnosticSnapshot() => new(
            false,
            false,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            null);

        public void Dispose()
        {
        }
    }

    private sealed class FakeWorkspace(
        List<string> log,
        WorkspaceActionHideResult result) : IWorkspaceCommands
    {
        public event EventHandler? ShutdownRequested;

        public Task StartAsync() => Task.CompletedTask;

        public Task<bool> HideForLauncherActionAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<WorkspaceActionHideResult> HideForPasteActionAsync(
            CancellationToken cancellationToken = default)
        {
            log.Add("hide");
            return Task.FromResult(result);
        }

        public void EnsureVisible(
            ForegroundTarget foregroundTarget,
            long requestedTimestamp,
            WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent)
        {
        }

        public void RequestExit() => ShutdownRequested?.Invoke(this, EventArgs.Empty);

        public void ReportFault(Exception exception) => _ = exception;
    }

    private sealed class FakeInput(List<string> log, bool succeeds) : IPasteInputService
    {
        public int CallCount { get; private set; }

        public Task<bool> InjectPasteAsync(
            ForegroundTarget expectedTarget,
            CancellationToken cancellationToken = default)
        {
            log.Add("inject");
            CallCount++;
            return Task.FromResult(succeeds);
        }
    }
}
