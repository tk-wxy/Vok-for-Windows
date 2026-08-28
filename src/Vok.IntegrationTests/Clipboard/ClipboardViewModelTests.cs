using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardViewModelTests
{
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
}
