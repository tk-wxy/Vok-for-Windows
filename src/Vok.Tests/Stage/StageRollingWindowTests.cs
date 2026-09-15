using Vok.Core.Stage;
using Xunit;

namespace Vok.Tests.Stage;

public sealed class StageRollingWindowTests
{
    [Fact]
    public void DefaultsNameTheBoundedThreeSegmentWindow()
    {
        var window = new StageRollingWindow();

        Assert.Equal(64, window.SegmentSize);
        Assert.Equal(3, window.MaximumSegmentCount);
        Assert.Equal(192, window.MaximumItemCount);
        Assert.False(window.IsInitialized);
        Assert.Empty(window.Items);
        Assert.Throws<InvalidOperationException>(
            () => window.CreateLoadRequest(StagePageDirection.TowardOlder));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void NonPositiveWindowBoundsAreRejected(
        int segmentSize,
        int maximumSegmentCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageRollingWindow(segmentSize, maximumSegmentCount));
    }

    [Fact]
    public void WindowItemBoundOverflowIsRejected()
    {
        Assert.Throws<OverflowException>(
            () => new StageRollingWindow(int.MaxValue, 2));
    }

    [Fact]
    public void ResetSnapshotsTheNewestSegmentAndReportsReplacementDelta()
    {
        var allItems = CreateItems(6);
        var source = allItems.Take(4).ToList();
        var page = LoadPage(allItems, null, StagePageDirection.TowardOlder, 4);
        page = page with { Items = source };
        var window = new StageRollingWindow(segmentSize: 4, maximumSegmentCount: 3);

        var firstChange = window.Reset(page);
        source.Clear();

        Assert.True(window.IsInitialized);
        Assert.Equal(1, window.Generation);
        Assert.Equal(1, window.SegmentCount);
        Assert.Equal(allItems.Take(4).Select(static item => item.Id),
            window.Items.Select(static item => item.Id));
        Assert.True(window.HasOlder);
        Assert.False(window.HasNewer);
        Assert.True(firstChange.IsReset);
        Assert.Equal(4, firstChange.AddedItems.Count);
        Assert.Empty(firstChange.EvictedItems);

        var replacement = LoadPage(allItems.Take(3).ToArray(), null,
            StagePageDirection.TowardOlder, 4);
        var secondChange = window.Reset(replacement);

        Assert.Equal(2, window.Generation);
        Assert.Equal(3, window.Items.Count);
        Assert.Equal(4, secondChange.EvictedItems.Count);
        Assert.Equal(3, secondChange.AddedItems.Count);
    }

    [Fact]
    public void EmptyResetProducesOneStableSegmentWithoutLoadRequests()
    {
        var window = new StageRollingWindow(segmentSize: 4, maximumSegmentCount: 3);

        window.Reset(LoadPage([], null, StagePageDirection.TowardOlder, 4));

        Assert.True(window.IsInitialized);
        Assert.Equal(1, window.SegmentCount);
        Assert.Empty(window.Items);
        Assert.Equal(0, window.TotalCount);
        Assert.Null(window.CreateLoadRequest(StagePageDirection.TowardNewer));
        Assert.Null(window.CreateLoadRequest(StagePageDirection.TowardOlder));
    }

    [Fact]
    public void OlderLoadsAppendAndFourthSegmentEvictsFromTheNewerEdge()
    {
        var allItems = CreateItems(20);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);

        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
        var fourth = ApplyNext(window, allItems, StagePageDirection.TowardOlder);

        Assert.Equal(StageRollingWindowApplyStatus.Applied, fourth.Status);
        var change = Assert.IsType<StageRollingWindowChange>(fourth.Change);
        Assert.Equal(StageRollingWindowEdge.Older, change.AddedAt);
        Assert.Equal(StageRollingWindowEdge.Newer, change.EvictedFrom);
        Assert.Equal(allItems.Take(4).Select(static item => item.Id),
            change.EvictedItems.Select(static item => item.Id));
        Assert.Equal(allItems.Skip(4).Take(12).Select(static item => item.Id),
            window.Items.Select(static item => item.Id));
        Assert.Equal(3, window.SegmentCount);
        Assert.Equal(12, window.Items.Count);
        Assert.True(window.HasNewer);
        Assert.True(window.HasOlder);
    }

    [Fact]
    public void NewerLoadPrependsAndEvictsFromTheOlderEdge()
    {
        var allItems = CreateItems(20);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);

        var result = ApplyNext(window, allItems, StagePageDirection.TowardNewer);

        Assert.Equal(StageRollingWindowApplyStatus.Applied, result.Status);
        var change = Assert.IsType<StageRollingWindowChange>(result.Change);
        Assert.Equal(StageRollingWindowEdge.Newer, change.AddedAt);
        Assert.Equal(StageRollingWindowEdge.Older, change.EvictedFrom);
        Assert.Equal(allItems.Skip(12).Take(4).Select(static item => item.Id),
            change.EvictedItems.Select(static item => item.Id));
        Assert.Equal(allItems.Take(12).Select(static item => item.Id),
            window.Items.Select(static item => item.Id));
    }

    [Fact]
    public void PartialOldestSegmentClosesTheOlderEdgeWithoutPadding()
    {
        var allItems = CreateItems(10);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);

        var result = ApplyNext(window, allItems, StagePageDirection.TowardOlder);

        Assert.Equal(StageRollingWindowApplyStatus.Applied, result.Status);
        Assert.Equal(2, result.Change!.AddedItems.Count);
        Assert.Equal(10, window.Items.Count);
        Assert.False(window.HasOlder);
        Assert.Null(window.CreateLoadRequest(StagePageDirection.TowardOlder));
    }

    [Fact]
    public void EqualSortOrdersUseGuidTieBreakersAcrossBothDirections()
    {
        var allItems = CreateItems(20, equalSortOrders: true);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        while (window.CreateLoadRequest(StagePageDirection.TowardOlder) is not null)
        {
            _ = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
        }

        while (window.CreateLoadRequest(StagePageDirection.TowardNewer) is not null)
        {
            _ = ApplyNext(window, allItems, StagePageDirection.TowardNewer);
        }

        Assert.Equal(allItems.Take(12).Select(static item => item.Id),
            window.Items.Select(static item => item.Id));
        Assert.True(IsStrictlyDescending(window.Items));
        Assert.False(window.HasNewer);
        Assert.True(window.HasOlder);
    }

    [Fact]
    public void TenThousandItemRoundTripKeepsAtMostThreeSegments()
    {
        var allItems = CreateItems(10_000, equalSortOrders: true);
        var window = ResetWindow(
            allItems,
            StageRollingWindow.DefaultSegmentSize,
            StageRollingWindow.DefaultMaximumSegmentCount);
        var visitedOlder = window.Items.Select(static item => item.Id).ToHashSet();
        var maximumObserved = window.Items.Count;

        while (window.CreateLoadRequest(StagePageDirection.TowardOlder) is not null)
        {
            var result = ApplyNext(window, allItems, StagePageDirection.TowardOlder);
            Assert.Equal(StageRollingWindowApplyStatus.Applied, result.Status);
            visitedOlder.UnionWith(result.Change!.AddedItems.Select(static item => item.Id));
            maximumObserved = Math.Max(maximumObserved, window.Items.Count);
        }

        Assert.Equal(10_000, visitedOlder.Count);
        Assert.True(maximumObserved <= 192);
        Assert.True(window.Items.Count <= 192);
        Assert.False(window.HasOlder);

        var visitedNewer = window.Items.Select(static item => item.Id).ToHashSet();
        while (window.CreateLoadRequest(StagePageDirection.TowardNewer) is not null)
        {
            var result = ApplyNext(window, allItems, StagePageDirection.TowardNewer);
            Assert.Equal(StageRollingWindowApplyStatus.Applied, result.Status);
            visitedNewer.UnionWith(result.Change!.AddedItems.Select(static item => item.Id));
            maximumObserved = Math.Max(maximumObserved, window.Items.Count);
        }

        Assert.Equal(10_000, visitedNewer.Count);
        Assert.True(maximumObserved <= 192);
        Assert.Equal(allItems.Take(192).Select(static item => item.Id),
            window.Items.Select(static item => item.Id));
        Assert.False(window.HasNewer);
    }

    [Fact]
    public void ResetMakesAnOutstandingRequestStaleWithoutApplyingItsPage()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var response = LoadPage(allItems, request.Anchor, request.Direction, 4);

        window.Reset(LoadPage(allItems, null, StagePageDirection.TowardOlder, 4));
        var before = window.Items;
        var result = window.Apply(request, response);

        Assert.Equal(StageRollingWindowApplyStatus.Stale, result.Status);
        Assert.Null(result.Change);
        Assert.Same(before, window.Items);
        Assert.Equal(2, window.Generation);
    }

    [Fact]
    public void FirstDuplicateRequestWinsAndTheSecondCannotDuplicateItems()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var firstRequest = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var secondRequest = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var response = LoadPage(allItems, firstRequest.Anchor, firstRequest.Direction, 4);

        var first = window.Apply(firstRequest, response);
        var second = window.Apply(secondRequest, response);

        Assert.Equal(StageRollingWindowApplyStatus.Applied, first.Status);
        Assert.Equal(StageRollingWindowApplyStatus.Stale, second.Status);
        Assert.Equal(8, window.Items.Count);
        Assert.Equal(8, window.Items.Select(static item => item.Id).Distinct().Count());
    }

    [Fact]
    public void RequestFromAnotherWindowIsRejectedEvenWhenItsGenerationAndEdgeMatch()
    {
        var allItems = CreateItems(12);
        var first = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var second = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            first.CreateLoadRequest(StagePageDirection.TowardOlder));
        var response = LoadPage(allItems, request.Anchor, request.Direction, 4);

        var result = second.Apply(request, response);

        Assert.Equal(StageRollingWindowApplyStatus.Stale, result.Status);
        Assert.Equal(4, second.Items.Count);
    }

    [Fact]
    public void TotalCountDriftInvalidatesAllRequestsAndRequiresReset()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var response = LoadPage(allItems, request.Anchor, request.Direction, 4) with
        {
            TotalCount = allItems.Count + 1,
        };
        var before = window.Items;

        var result = window.Apply(request, response);

        Assert.Equal(StageRollingWindowApplyStatus.ResetRequired, result.Status);
        Assert.Null(result.Change);
        Assert.True(window.RequiresReset);
        Assert.Equal(2, window.Generation);
        Assert.Same(before, window.Items);
        Assert.Null(window.CreateLoadRequest(StagePageDirection.TowardOlder));

        window.Reset(LoadPage(allItems, null, StagePageDirection.TowardOlder, 4));
        Assert.False(window.RequiresReset);
        Assert.Equal(3, window.Generation);
    }

    [Fact]
    public void JoinBoundaryDriftRequiresResetWithoutPartiallyApplyingItems()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var response = LoadPage(allItems, request.Anchor, request.Direction, 4);
        var firstLoaded = response.Items[0];
        response = response with
        {
            NewerBoundary = new StageOrderAnchor(
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                1,
                firstLoaded.SortOrder + 1),
        };
        var before = window.Items;

        var result = window.Apply(request, response);

        Assert.Equal(StageRollingWindowApplyStatus.ResetRequired, result.Status);
        Assert.True(window.RequiresReset);
        Assert.Same(before, window.Items);
    }

    [Fact]
    public void EmptyResponseFromAFormerEdgeRequiresResetInsteadOfLookingLikeCorruption()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var emptyResponse = new StagePage(
            [],
            NewerCursor: null,
            OlderCursor: null,
            TotalCount: 11);
        var before = window.Items;

        var result = window.Apply(request, emptyResponse);

        Assert.Equal(StageRollingWindowApplyStatus.ResetRequired, result.Status);
        Assert.True(window.RequiresReset);
        Assert.Same(before, window.Items);
    }

    [Fact]
    public void StructurallyInvalidPageIsRejectedAtomically()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(StagePageDirection.TowardOlder));
        var valid = LoadPage(allItems, request.Anchor, request.Direction, 4);
        var invalidItems = valid.Items.Reverse().ToArray();
        var invalid = valid with { Items = invalidItems };
        var before = window.Items;
        var generation = window.Generation;

        Assert.Throws<InvalidOperationException>(() => window.Apply(request, invalid));

        Assert.Same(before, window.Items);
        Assert.Equal(generation, window.Generation);
        Assert.False(window.RequiresReset);
    }

    [Fact]
    public void OversizedResetAndInvalidDirectionLeaveExistingStateUntouched()
    {
        var allItems = CreateItems(12);
        var window = ResetWindow(allItems, segmentSize: 4, maximumSegmentCount: 3);
        var oversized = LoadPage(allItems, null, StagePageDirection.TowardOlder, 5);
        var before = window.Items;
        var generation = window.Generation;

        Assert.Throws<InvalidOperationException>(() => window.Reset(oversized));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            window.CreateLoadRequest((StagePageDirection)int.MaxValue));

        Assert.Same(before, window.Items);
        Assert.Equal(generation, window.Generation);
    }

    private static StageRollingWindow ResetWindow(
        IReadOnlyList<StageItem> allItems,
        int segmentSize,
        int maximumSegmentCount)
    {
        var window = new StageRollingWindow(segmentSize, maximumSegmentCount);
        window.Reset(LoadPage(
            allItems,
            null,
            StagePageDirection.TowardOlder,
            segmentSize));
        return window;
    }

    private static StageRollingWindowApplyResult ApplyNext(
        StageRollingWindow window,
        IReadOnlyList<StageItem> allItems,
        StagePageDirection direction)
    {
        var request = Assert.IsType<StageRollingWindowLoadRequest>(
            window.CreateLoadRequest(direction));
        var page = LoadPage(allItems, request.Anchor, direction, window.SegmentSize);
        return window.Apply(request, page);
    }

    private static StagePage LoadPage(
        IReadOnlyList<StageItem> allItems,
        StagePageCursor? anchor,
        StagePageDirection direction,
        int pageSize)
    {
        var ordered = allItems
            .OrderByDescending(static item => ToOrderAnchor(item))
            .ToArray();
        IEnumerable<StageItem> candidates = direction switch
        {
            StagePageDirection.TowardOlder => ordered.Where(item =>
                anchor is null || Compare(item, anchor.Value) < 0),
            StagePageDirection.TowardNewer => ordered
                .Where(item => anchor is not null && Compare(item, anchor.Value) > 0)
                .Reverse(),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        var loaded = candidates.Take(pageSize).ToArray();
        if (direction == StagePageDirection.TowardNewer)
        {
            Array.Reverse(loaded);
        }

        if (loaded.Length == 0)
        {
            return new StagePage([], null, null, ordered.LongLength);
        }

        var firstIndex = Array.FindIndex(ordered, item => item.Id == loaded[0].Id);
        var lastIndex = Array.FindIndex(ordered, item => item.Id == loaded[^1].Id);
        StageOrderAnchor? newerBoundary = firstIndex > 0
            ? ToOrderAnchor(ordered[firstIndex - 1])
            : null;
        StageOrderAnchor? olderBoundary = lastIndex < ordered.Length - 1
            ? ToOrderAnchor(ordered[lastIndex + 1])
            : null;
        return new StagePage(
            loaded,
            newerBoundary is null ? null : ToCursor(loaded[0]),
            olderBoundary is null ? null : ToCursor(loaded[^1]),
            ordered.LongLength,
            newerBoundary,
            olderBoundary);
    }

    private static IReadOnlyList<StageItem> CreateItems(
        int count,
        bool equalSortOrders = false)
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(1, count)
            .Select(index => new StageItem(
                Guid.Parse($"10000000-0000-0000-0000-{index:D12}"),
                StageItemKind.File,
                $@"C:\Stage\item-{index:D5}.txt",
                $@"c:\stage\item-{index:D5}.txt",
                $"item-{index:D5}.txt",
                equalSortOrders ? 100 : count - index + 1,
                IsPinned: false,
                Revision: 1,
                now,
                now))
            .OrderByDescending(static item => ToOrderAnchor(item))
            .ToArray();
    }

    private static bool IsStrictlyDescending(IReadOnlyList<StageItem> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            if (ToOrderAnchor(items[index - 1]).CompareTo(ToOrderAnchor(items[index])) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(StageItem item, StagePageCursor cursor)
    {
        var orderComparison = item.SortOrder.CompareTo(cursor.SortOrder);
        return orderComparison != 0
            ? orderComparison
            : StringComparer.Ordinal.Compare(
                item.Id.ToString("D"),
                cursor.Id.ToString("D"));
    }

    private static StagePageCursor ToCursor(StageItem item) =>
        new(item.SortOrder, item.Id);

    private static StageOrderAnchor ToOrderAnchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);
}
