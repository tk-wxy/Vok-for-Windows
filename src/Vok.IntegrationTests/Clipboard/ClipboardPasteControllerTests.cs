using System.IO;
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

    [Fact]
    public async Task ForceCopyRewritesMoveIntentWithoutHidingOrInjecting()
    {
        var log = new List<string>();
        var entry = FileEntry(ClipboardFileTransferIntent.Move);
        var payload = FilePayload(entry, ClipboardFileTransferIntent.Move);
        var repository = new FakeRepository(entry, log, payload);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var input = new FakeInput(log, succeeds: true);
        using var presentation = new FakePresentation(log, ClipboardFileAvailability.Available);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            input,
            presentation);

        await viewModel.CopyFileCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "inspect", "write", "verify"], log);
        Assert.Equal(ClipboardFileTransferIntent.Copy, platform.LastPayload?.FileIntent);
        Assert.Equal(
            ClipboardFileSemanticIdentity.Compute(
                ClipboardFileTransferIntent.Copy,
                payload.FileReferences),
            platform.LastPayload?.SemanticHash);
        Assert.Equal(0, input.CallCount);
        Assert.Equal("已复制文件列表；不会移动源文件。", viewModel.StatusText);
    }

    [Fact]
    public async Task MoveRestoreFailureUsesExplicitCutWarning()
    {
        var log = new List<string>();
        var entry = FileEntry(ClipboardFileTransferIntent.Move);
        var repository = new FakeRepository(
            entry,
            log,
            FilePayload(entry, ClipboardFileTransferIntent.Move));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var presentation = new FakePresentation(log, ClipboardFileAvailability.Available);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, WorkspaceActionHideResult.NotHidden),
            new FakeInput(log, succeeds: true),
            presentation);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "inspect", "write", "hide"], log);
        Assert.Equal(ClipboardFileTransferIntent.Move, platform.LastPayload?.FileIntent);
        Assert.Contains("已写入剪切", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("移动源文件", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFileStopsBeforeClipboardWrite()
    {
        var log = new List<string>();
        var entry = FileEntry(ClipboardFileTransferIntent.Copy);
        var repository = new FakeRepository(
            entry,
            log,
            FilePayload(entry, ClipboardFileTransferIntent.Copy));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var presentation = new FakePresentation(log, ClipboardFileAvailability.Missing);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true),
            presentation);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "inspect"], log);
        Assert.Equal(0, platform.WriteCount);
        Assert.Contains("源文件已失效", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownIntentMainActionWritesCopyBeforeSafePasteSequence()
    {
        var log = new List<string>();
        var entry = FileEntry(ClipboardFileTransferIntent.Unknown);
        var payload = FilePayload(entry, ClipboardFileTransferIntent.Unknown);
        var repository = new FakeRepository(entry, log, payload);
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var presentation = new FakePresentation(
            log,
            ClipboardFileAvailability.Unknown);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true),
            presentation);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(
            ["materialize", "inspect", "write", "hide", "verify", "inject"],
            log);
        Assert.Equal(ClipboardFileTransferIntent.Copy, platform.LastPayload?.FileIntent);
        Assert.Equal(
            ClipboardFileSemanticIdentity.Compute(
                ClipboardFileTransferIntent.Copy,
                payload.FileReferences),
            platform.LastPayload?.SemanticHash);
    }

    [Fact]
    public async Task ImagePasteMaterializesOriginalBeforeWriteAndReusesSafePasteSequence()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var materializer = new FakeImageMaterializer(log);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            materializer);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(
            ["materialize", "materialize-image", "write", "hide", "verify", "inject"],
            log);
        Assert.Equal(0, platform.WriteCount);
        Assert.Equal(1, platform.ImageWriteCount);
        Assert.True(materializer.LastCanonicalLease?.IsDisposed is true);
        Assert.True(materializer.LastDibLease?.IsDisposed is true);
        Assert.Equal("已粘贴到呼出前目标。", viewModel.StatusText);
    }

    [Fact]
    public async Task ImageMaterializationFailureKeepsWorkspaceVisibleAndDoesNotWrite()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var materializer = new FakeImageMaterializer(log) { Fail = true };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            materializer);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(["materialize", "materialize-image"], log);
        Assert.Equal(0, platform.WriteCount);
        Assert.Equal(0, platform.ImageWriteCount);
        Assert.Contains("无法写入剪贴板", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeCancelsActiveImageMaterializationBeforeClipboardWrite()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        using var materializer = new FakeImageMaterializer(log) { Hold = true };
        var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            new FakeWorkspace(log, CanInjectHide()),
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            materializer);

        var action = viewModel.PasteCommand.ExecuteAsync(
            new ClipboardItemViewModel(entry));
        await materializer.Started.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        controller.Dispose();
        await action;

        Assert.Equal(["materialize", "materialize-image"], log);
        Assert.Equal(0, platform.ImageWriteCount);
    }

    [Fact]
    public async Task ImageFolderBranchCreatesPngWithoutTouchingClipboardOrInput()
    {
        var log = new List<string>();
        var capturedUtc = new DateTimeOffset(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);
        var entry = ImageEntry(capturedUtc);
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, CanInjectHide())
        {
            SupportsImageTarget = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(
            ["materialize", "claim-target", "resolve-destination", "materialize-export", "create-file", "hide-created"],
            log);
        Assert.Equal(0, platform.WriteCount);
        Assert.Equal(0, platform.ImageWriteCount);
        Assert.Equal(
            $"Vok 图片 {capturedUtc.ToLocalTime():yyyy-MM-dd HHmmss}.png",
            destination.RequestedFileName);
        Assert.True(export.LastLease?.IsDisposed is true);
        Assert.Contains("已创建图片文件", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedImageDestinationFallsBackToExistingSafePaste()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, CanInjectHide())
        {
            SupportsImageTarget = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log)
        {
            ResolveSupported = false,
        };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(
            ["materialize", "claim-target", "resolve-destination", "materialize-image", "write", "hide", "verify", "inject"],
            log);
        Assert.Equal(1, platform.ImageWriteCount);
        Assert.Null(export.LastLease);
    }

    [Fact]
    public async Task StaleImageDestinationCreatesNothingAndKeepsWorkspaceVisible()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, CanInjectHide())
        {
            SupportsImageTarget = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log)
        {
            CreationStatus = ClipboardImageFileCreationStatus.StaleTarget,
        };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.DoesNotContain("hide-created", log);
        Assert.DoesNotContain("write", log);
        Assert.Contains("目标文件夹已变化", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(workspace.LastImageClaim?.IsFileCreationAuthorized);
        Assert.True(export.LastLease?.IsDisposed is true);
    }

    [Fact]
    public async Task CreatedImageIsReportedOnceWhenWorkspaceHideFails()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, WorkspaceActionHideResult.NotHidden)
        {
            SupportsImageTarget = true,
            ThrowWhenHidingCreatedImage = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log);
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(1, destination.CreateCount);
        Assert.DoesNotContain("write", log);
        Assert.Contains("已创建", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("不会重复创建", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedInvocationWhileImageCreateIsActiveCreatesOnlyOnce()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var workspace = new FakeWorkspace(log, CanInjectHide())
        {
            SupportsImageTarget = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log)
        {
            HoldCreate = true,
        };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            new FakePlatform(log),
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);
        var item = new ClipboardItemViewModel(entry);

        var first = viewModel.PasteCommand.ExecuteAsync(item);
        await destination.CreateStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await viewModel.PasteCommand.ExecuteAsync(item);
        destination.ReleaseCreate.TrySetResult();
        await first;

        Assert.Equal(1, destination.CreateCount);
        Assert.Equal(1, log.Count(value => value == "materialize-export"));
    }

    [Fact]
    public async Task FailureAfterFileAuthorizationNeverFallsBackToClipboard()
    {
        var log = new List<string>();
        var entry = ImageEntry();
        var repository = new FakeRepository(entry, log, ImagePayload(entry));
        var viewModel = new ClipboardViewModel(repository);
        var platform = new FakePlatform(log);
        var workspace = new FakeWorkspace(log, CanInjectHide())
        {
            SupportsImageTarget = true,
        };
        using var replay = new FakeImageMaterializer(log);
        using var export = new FakeImageExportMaterializer(log);
        using var destination = new FakeImageDestinationService(log)
        {
            ThrowAfterAuthorization = true,
        };
        using var controller = new ClipboardPasteController(
            viewModel,
            repository,
            platform,
            workspace,
            new FakeInput(log, succeeds: true),
            filePresentation: null,
            replay,
            export,
            destination);

        await viewModel.PasteCommand.ExecuteAsync(new ClipboardItemViewModel(entry));

        Assert.Equal(1, destination.CreateCount);
        Assert.Equal(0, platform.ImageWriteCount);
        Assert.DoesNotContain("hide-created", log);
        Assert.Contains("请先检查目标目录", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("避免重复创建", viewModel.StatusText, StringComparison.Ordinal);
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

    private static ClipboardEntry FileEntry(ClipboardFileTransferIntent intent)
    {
        var files = FileReferences();
        return new ClipboardEntry(
            Guid.NewGuid(),
            ClipboardContentKind.FileList,
            ClipboardFileSemanticIdentity.Compute(intent, files),
            "A.txt",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            1,
            false,
            1,
            intent,
            files.Count);
    }

    private static ClipboardEntry ImageEntry(DateTimeOffset? capturedUtc = null)
    {
        var timestamp = capturedUtc ?? DateTimeOffset.UtcNow;
        var primary = new ClipboardAssetReference(
            new string('e', 64),
            ClipboardAssetState.Ready,
            3);
        var thumbnail = new ClipboardAssetReference(
            new string('f', 64),
            ClipboardAssetState.Ready,
            4);
        return new ClipboardEntry(
            Guid.NewGuid(),
            ClipboardContentKind.Image,
            new string('a', 64),
            "1×1",
            timestamp,
            timestamp,
            1,
            1,
            false,
            1,
            Image: new ClipboardImageMetadata(
                1,
                1,
                HasAlpha: false,
                DpiXMilli: null,
                DpiYMilli: null,
                primary,
                thumbnail));
    }

    private static ClipboardReplayPayload Payload(ClipboardEntry entry) => new(
        ClipboardContentKind.Text,
        entry.SemanticHash,
        [new ClipboardStoredRepresentation(
            ClipboardRepresentationKind.UnicodeText,
            100,
            entry.PlainPreview,
            12)]);

    private static ClipboardReplayPayload FilePayload(
        ClipboardEntry entry,
        ClipboardFileTransferIntent intent)
    {
        var files = FileReferences();
        return new ClipboardReplayPayload(
            ClipboardContentKind.FileList,
            ClipboardFileSemanticIdentity.Compute(intent, files),
            [],
            intent,
            files);
    }

    private static ClipboardReplayPayload ImagePayload(ClipboardEntry entry) => new(
        ClipboardContentKind.Image,
        entry.SemanticHash,
        [],
        Image: entry.Image);

    private static IReadOnlyList<ClipboardFileReference> FileReferences() =>
        [new ClipboardFileReference(0, @"C:\A.txt", @"C:\A.txt", "A.txt")];

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

    private sealed class FakeRepository(
        ClipboardEntry entry,
        List<string> log,
        ClipboardReplayPayload? replayPayload = null)
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

        public Task<ClipboardEntry> UpsertFileListAsync(
            ClipboardFileListContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(entry);

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            log.Add("materialize");
            return Task.FromResult<ClipboardReplayPayload?>(replayPayload ?? Payload(entry));
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

        public int ImageWriteCount { get; private set; }

        public ClipboardReplayPayload? LastPayload { get; private set; }

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
            LastPayload = payload;
            WriteStarted.TrySetResult();
            if (HoldWrite)
            {
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            return new ClipboardWriteReceipt(10, operationId, payload.SemanticHash);
        }

        public async Task<ClipboardWriteReceipt> WriteImageAsync(
            ClipboardImageReplayMaterialization materialization,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            log.Add("write");
            ImageWriteCount++;
            WriteStarted.TrySetResult();
            if (HoldWrite)
            {
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            return new ClipboardWriteReceipt(
                10,
                operationId,
                materialization.SemanticHash);
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
        public bool SupportsImageTarget { get; init; }

        public bool ThrowWhenHidingCreatedImage { get; init; }

        public WorkspacePasteTargetClaim? LastImageClaim { get; private set; }

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

        public Task<WorkspacePasteTargetClaim?> AcquirePasteTargetClaimAsync(
            CancellationToken cancellationToken = default)
        {
            if (!SupportsImageTarget)
            {
                return Task.FromResult<WorkspacePasteTargetClaim?>(null);
            }

            log.Add("claim-target");
            LastImageClaim = new WorkspacePasteTargetClaim(
                7,
                CanInjectHide().Target);
            return Task.FromResult<WorkspacePasteTargetClaim?>(LastImageClaim);
        }

        public Task<WorkspaceActionHideResult> HideAfterImageFileCreatedAsync(
            WorkspacePasteTargetClaim claim,
            CancellationToken cancellationToken = default)
        {
            log.Add("hide-created");
            if (ThrowWhenHidingCreatedImage)
            {
                throw new InvalidOperationException("hide failed");
            }

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

    private sealed class FakePresentation(
        List<string> log,
        ClipboardFileAvailability availability) : IClipboardFilePresentationService
    {
        public Task<ClipboardFilePresentation> InspectAsync(
            IReadOnlyList<ClipboardFileReference> files,
            int pixelSize,
            CancellationToken cancellationToken = default)
        {
            _ = files;
            _ = pixelSize;
            log.Add("inspect");
            return Task.FromResult(new ClipboardFilePresentation(null, availability));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeImageMaterializer(List<string> log)
        : IClipboardImageReplayMaterializer
    {
        public bool Fail { get; init; }

        public bool Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TrackingLease? LastCanonicalLease { get; private set; }

        public TrackingLease? LastDibLease { get; private set; }

        public async Task<ClipboardImageReplayMaterialization> MaterializeAsync(
            ClipboardReplayPayload descriptor,
            CancellationToken cancellationToken = default)
        {
            log.Add("materialize-image");
            Started.TrySetResult();
            if (Hold)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Fail)
            {
                throw new InvalidDataException("broken image");
            }

            LastCanonicalLease = new TrackingLease([1, 2, 3, 4]);
            LastDibLease = new TrackingLease(new byte[128]);
            return new ClipboardImageReplayMaterialization(
                descriptor.SemanticHash,
                1,
                1,
                hasAlpha: false,
                dpiXMilli: null,
                dpiYMilli: null,
                LastCanonicalLease,
                LastDibLease);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeImageExportMaterializer(List<string> log)
        : IClipboardImageExportMaterializer
    {
        public TrackingLease? LastLease { get; private set; }

        public Task<ClipboardImageExportMaterialization> MaterializeForExportAsync(
            ClipboardReplayPayload descriptor,
            CancellationToken cancellationToken = default)
        {
            log.Add("materialize-export");
            LastLease = new TrackingLease([1, 2, 3, 4]);
            return Task.FromResult(new ClipboardImageExportMaterialization(
                new string('e', 64),
                descriptor.SemanticHash,
                1,
                1,
                hasAlpha: false,
                LastLease));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeImageDestinationService(List<string> log)
        : IClipboardImageDestinationService
    {
        public bool ResolveSupported { get; init; } = true;

        public ClipboardImageFileCreationStatus CreationStatus { get; init; } =
            ClipboardImageFileCreationStatus.Created;

        public bool HoldCreate { get; init; }

        public bool ThrowAfterAuthorization { get; init; }

        public int CreateCount { get; private set; }

        public string? RequestedFileName { get; private set; }

        public TaskCompletionSource CreateStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCreate { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IClipboardImageDestinationClaim?> ResolveAsync(
            ForegroundTarget target,
            CancellationToken cancellationToken = default)
        {
            log.Add("resolve-destination");
            return Task.FromResult<IClipboardImageDestinationClaim?>(
                ResolveSupported ? new FakeDestinationClaim() : null);
        }

        public async Task<ClipboardImageFileCreationResult> CreateAsync(
            IClipboardImageDestinationClaim destination,
            ClipboardImageExportMaterialization materialization,
            string requestedFileName,
            IClipboardImageFileCreationAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            log.Add("create-file");
            CreateCount++;
            RequestedFileName = requestedFileName;
            CreateStarted.TrySetResult();
            if (HoldCreate)
            {
                await ReleaseCreate.Task.WaitAsync(cancellationToken);
            }

            if (CreationStatus is ClipboardImageFileCreationStatus.StaleTarget)
            {
                return ClipboardImageFileCreationResult.StaleTarget;
            }

            Assert.True(authorization.TryClaimFileCreation());
            if (ThrowAfterAuthorization)
            {
                throw new InvalidOperationException("post-copy evidence unavailable");
            }

            return new ClipboardImageFileCreationResult(
                ClipboardImageFileCreationStatus.Created,
                requestedFileName,
                TemporaryFilesCleaned: true);
        }

        public void Dispose()
        {
        }

        private sealed class FakeDestinationClaim : IClipboardImageDestinationClaim
        {
            public ClipboardImageDestinationKind Kind =>
                ClipboardImageDestinationKind.FileSystemFolder;

            public void Dispose()
            {
            }
        }
    }

    private sealed class TrackingLease(byte[] bytes) : IClipboardPayloadLease
    {
        private int _disposed;

        public long Length => bytes.LongLength;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
