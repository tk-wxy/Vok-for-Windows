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

    private sealed class FakeRepository(ClipboardPage page) : IClipboardHistoryRepository
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

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardReplayPayload?>(null);

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
}
