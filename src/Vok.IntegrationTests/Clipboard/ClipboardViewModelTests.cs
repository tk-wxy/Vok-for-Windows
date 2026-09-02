using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardViewModelTests
{
    [Fact]
    public void EmptyStateDeclaresImagesOnlyWhenProductImageFeatureIsEnabled()
    {
        var repository = new FakeRepository(
            new ClipboardPage([], null, TotalCount: 0));
        using var disabled = new ClipboardViewModel(repository);
        using var enabled = new ClipboardViewModel(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: true,
            imageFeatureUnavailableStatus: null);
        using var degraded = new ClipboardViewModel(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: false,
            imageFeatureUnavailableStatus: "图片历史暂不可用；文本和文件仍可使用。");

        Assert.DoesNotContain("图片", disabled.EmptyStateText, StringComparison.Ordinal);
        Assert.Contains("图片", enabled.EmptyStateText, StringComparison.Ordinal);
        Assert.Contains("暂不可用", degraded.EmptyStateText, StringComparison.Ordinal);
        degraded.MarkListenerReady();
        Assert.Contains("暂不可用", degraded.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateFromUnloadedPageMovesToFrontWithoutInflatingTotalCount()
    {
        var loaded = Entry("loaded", captureCount: 1, order: 100);
        var repository = new FakeRepository(
            new ClipboardPage([loaded], null, TotalCount: 100));
        using var viewModel = new ClipboardViewModel(repository);
        await viewModel.InitializeAsync();

        var repeated = Entry("old duplicate", captureCount: 2, order: 101);
        viewModel.ApplyCaptured(repeated);

        Assert.Equal(100, viewModel.TotalCount);
        Assert.Equal(repeated.Id, viewModel.Items[0].Id);
        Assert.Equal(2, viewModel.Items.Count);

        var newEntry = Entry("new", captureCount: 1, order: 102);
        viewModel.ApplyCaptured(newEntry);
        Assert.Equal(101, viewModel.TotalCount);
    }

    [Fact]
    public async Task DeleteRequiresTwoCardLocalConfirmations()
    {
        var entry = Entry("delete me", captureCount: 1, order: 1);
        var repository = new FakeRepository(
            new ClipboardPage([entry], null, TotalCount: 1));
        using var viewModel = new ClipboardViewModel(repository);
        await viewModel.InitializeAsync();
        var item = Assert.Single(viewModel.Items);

        await viewModel.RequestRemoveAsync(item);

        Assert.True(item.IsDeleteArmed);
        Assert.Equal(0, repository.RemoveCallCount);
        Assert.Single(viewModel.Items);

        await viewModel.RequestRemoveAsync(item);

        Assert.Equal(1, repository.RemoveCallCount);
        Assert.Empty(viewModel.Items);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public async Task FileEntryLoadsPresentationAndExposesCopyOnlyAction()
    {
        var id = Guid.NewGuid();
        const string firstPath = @"C:\Vok tests\第一项.txt";
        const string secondPath = @"C:\Vok tests\second.txt";
        var files = new[]
        {
            new ClipboardFileReference(0, firstPath, firstPath, "第一项.txt"),
            new ClipboardFileReference(1, secondPath, secondPath, "second.txt"),
        };
        var hash = ClipboardFileSemanticIdentity.Compute(
            ClipboardFileTransferIntent.Move,
            files);
        var entry = new ClipboardEntry(
            id,
            ClipboardContentKind.FileList,
            hash,
            "第一项.txt + 另外 1 项",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            1,
            false,
            1,
            ClipboardFileTransferIntent.Move,
            2);
        var replay = new ClipboardReplayPayload(
            ClipboardContentKind.FileList,
            hash,
            [],
            ClipboardFileTransferIntent.Move,
            files);
        var repository = new FakeRepository(
            new ClipboardPage([entry], null, TotalCount: 1),
            new Dictionary<Guid, ClipboardReplayPayload> { [id] = replay });
        using var presentation = new FakePresentationService(
            ClipboardFileAvailability.Missing);
        using var viewModel = new ClipboardViewModel(repository, presentation);

        await viewModel.InitializeAsync();
        var item = Assert.Single(viewModel.Items);

        Assert.Equal(1, presentation.CallCount);
        Assert.Equal(firstPath, item.FileDetailsText);
        Assert.Equal("剪切", item.IntentText);
        Assert.Equal("源文件已失效", item.AvailabilityText);
        Assert.Contains("移动源文件", item.PasteToolTip);

        var copyCount = 0;
        viewModel.CopyFileActionAsync = _ =>
        {
            copyCount++;
            return Task.CompletedTask;
        };
        await viewModel.CopyFileCommand.ExecuteAsync(item);
        await viewModel.CopyFileCommand.ExecuteAsync(
            new ClipboardItemViewModel(Entry("text", 1, 2)));

        Assert.Equal(1, copyCount);
    }

    [Fact]
    public async Task ImageEntryStaysLightweightAndDeletesThroughAssetCoordinatorBoundary()
    {
        var hash = new string('a', 64);
        var thumbnail = new ClipboardAssetReference(
            new string('b', 64),
            ClipboardAssetState.Ready,
            4);
        var entry = new ClipboardEntry(
            Guid.NewGuid(),
            ClipboardContentKind.Image,
            hash,
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            1,
            false,
            7,
            Image: new ClipboardImageMetadata(
                1920,
                1080,
                true,
                96000,
                96000,
                new ClipboardAssetReference(
                    new string('c', 64),
                    ClipboardAssetState.Ready,
                    3),
                thumbnail));
        var repository = new FakeRepository(
            new ClipboardPage([entry], null, TotalCount: 1));
        using var imageAssets = new FakeImageAssetService();
        using var viewModel = new ClipboardViewModel(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets);

        await viewModel.InitializeAsync();
        var item = Assert.Single(viewModel.Items);

        Assert.True(item.IsImage);
        Assert.False(item.IsFileList);
        Assert.Equal("1920×1080", item.ImageDimensionsText);
        Assert.Contains("图片剪贴板", item.AutomationName);
        Assert.Null(item.Icon);
        item.SetThumbnailState(ClipboardThumbnailDisplayState.Unavailable);
        Assert.Contains("缩略图不可用", item.AutomationName);

        await viewModel.RequestRemoveAsync(item);
        await viewModel.RequestRemoveAsync(item);

        Assert.Equal(1, imageAssets.RemoveCallCount);
        Assert.Equal(0, repository.RemoveCallCount);
        Assert.Empty(viewModel.Items);
    }

    [Fact]
    public async Task ClearHistoryRequiresConfirmationAndRejectsPreClearPresentation()
    {
        var entry = Entry("clear me", captureCount: 1, order: 1);
        var repository = new FakeRepository(
            new ClipboardPage([entry], null, TotalCount: 1));
        var maintenance = new FakeMaintenanceService(
            new ClipboardHistoryMaintenanceResult(
                [entry.Id],
                RemainingEntryCount: 0,
                ManagedBytesBefore: 10,
                ManagedBytesAfter: 0,
                PendingAssetDeleteCount: 0,
                AssetCleanupDeferred: false));
        using var viewModel = new ClipboardViewModel(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: false,
            imageFeatureUnavailableStatus: null,
            maintenanceService: maintenance);
        await viewModel.InitializeAsync();
        var preClearEpoch = viewModel.CaptureHistoryEpoch();

        await viewModel.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Equal("确认清空", viewModel.ClearHistoryButtonText);
        Assert.Equal(0, maintenance.ClearCallCount);
        Assert.Single(viewModel.Items);

        await viewModel.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Equal(1, maintenance.ClearCallCount);
        Assert.Empty(viewModel.Items);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.False(viewModel.ApplyCaptured(
            Entry("stale", captureCount: 1, order: 2),
            preClearEpoch));
        Assert.Empty(viewModel.Items);
        Assert.Contains("系统剪贴板内容未改变", viewModel.StatusText, StringComparison.Ordinal);
    }

    private static ClipboardEntry Entry(string preview, int captureCount, long order) => new(
        Guid.NewGuid(),
        ClipboardContentKind.Text,
        order.ToString("x64"),
        preview,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        order,
        captureCount,
        false,
        captureCount);

    private sealed class FakeRepository(
        ClipboardPage page,
        IReadOnlyDictionary<Guid, ClipboardReplayPayload>? replayPayloads = null)
        : IClipboardHistoryRepository
    {
        public int RemoveCallCount { get; private set; }

        public Task<ClipboardPage> LoadPageAsync(
            ClipboardPageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default) => Task.FromResult(page);

        public Task<ClipboardEntry> UpsertTextAsync(
            ClipboardTextContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClipboardEntry> UpsertFileListAsync(
            ClipboardFileListContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) => Task.FromResult(
                replayPayloads is not null &&
                replayPayloads.TryGetValue(entryId, out var payload)
                    ? payload
                    : null);

        public Task<ClipboardEntry?> SetPinnedAsync(
            Guid entryId,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardEntry?>(null);

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            RemoveCallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePresentationService(
        ClipboardFileAvailability availability)
        : IClipboardFilePresentationService
    {
        public int CallCount { get; private set; }

        public Task<ClipboardFilePresentation> InspectAsync(
            IReadOnlyList<ClipboardFileReference> files,
            int pixelSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ClipboardFilePresentation(null, availability));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeImageAssetService : IClipboardImageAssetService
    {
        public int RemoveCallCount { get; private set; }

        public Task<ClipboardImageThumbnailResult?> LoadThumbnailAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardImageThumbnailResult?>(null);

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            RemoveCallCount++;
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeMaintenanceService(
        ClipboardHistoryMaintenanceResult clearResult)
        : IClipboardHistoryMaintenanceService
    {
        public int ClearCallCount { get; private set; }

        public Task<ClipboardHistoryMaintenanceResult> ApplyRetentionAsync(
            ClipboardRetentionOptions options,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardHistoryMaintenanceResult(
                [],
                clearResult.RemainingEntryCount,
                clearResult.ManagedBytesBefore,
                clearResult.ManagedBytesBefore,
                PendingAssetDeleteCount: 0,
                AssetCleanupDeferred: false));

        public Task<ClipboardHistoryMaintenanceResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default)
        {
            ClearCallCount++;
            return Task.FromResult(clearResult);
        }

        public Task<bool> RemoveEntryAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
