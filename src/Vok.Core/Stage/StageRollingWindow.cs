using System.Collections.ObjectModel;

namespace Vok.Core.Stage;

public enum StageRollingWindowEdge
{
    Newer = 0,
    Older = 1,
}

public enum StageRollingWindowApplyStatus
{
    Applied = 0,
    Stale = 1,
    ResetRequired = 2,
}

public sealed class StageRollingWindowLoadRequest
{
    internal StageRollingWindowLoadRequest(
        Guid ownerToken,
        long generation,
        StagePageDirection direction,
        StagePageCursor anchor,
        StageOrderAnchor joinBoundary)
    {
        OwnerToken = ownerToken;
        Generation = generation;
        Direction = direction;
        Anchor = anchor;
        JoinBoundary = joinBoundary;
    }

    internal Guid OwnerToken { get; }

    public long Generation { get; }

    public StagePageDirection Direction { get; }

    public StagePageCursor Anchor { get; }

    public StageOrderAnchor JoinBoundary { get; }
}

public sealed record StageRollingWindowChange(
    bool IsReset,
    StageRollingWindowEdge? AddedAt,
    IReadOnlyList<StageItem> AddedItems,
    StageRollingWindowEdge? EvictedFrom,
    IReadOnlyList<StageItem> EvictedItems);

public sealed record StageRollingWindowApplyResult(
    StageRollingWindowApplyStatus Status,
    StageRollingWindowChange? Change);

public sealed class StageRollingWindow
{
    public const int DefaultSegmentSize = 64;
    public const int DefaultMaximumSegmentCount = 3;

    private static readonly IReadOnlyList<StageItem> EmptyItems =
        Array.AsReadOnly(Array.Empty<StageItem>());

    private readonly Guid _ownerToken = Guid.NewGuid();
    private readonly List<Segment> _segments = [];
    private IReadOnlyList<StageItem> _items = EmptyItems;
    private int _maximumSegmentCount;
    private int _maximumItemCount;
    private long _generation;
    private bool _isInitialized;
    private bool _requiresReset;

    public StageRollingWindow(
        int segmentSize = DefaultSegmentSize,
        int maximumSegmentCount = DefaultMaximumSegmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSegmentCount, 1);

        SegmentSize = segmentSize;
        _maximumSegmentCount = maximumSegmentCount;
        _maximumItemCount = checked(segmentSize * maximumSegmentCount);
    }

    public int SegmentSize { get; }

    public int MaximumSegmentCount => _maximumSegmentCount;

    public int MaximumItemCount => _maximumItemCount;

    public bool IsInitialized => _isInitialized;

    public bool RequiresReset => _requiresReset;

    public long Generation => _generation;

    public int SegmentCount => _segments.Count;

    public IReadOnlyList<StageItem> Items => _items;

    public long TotalCount { get; private set; }

    public StagePageCursor? NewerCursor =>
        _isInitialized ? _segments[0].NewerCursor : null;

    public StagePageCursor? OlderCursor =>
        _isInitialized ? _segments[^1].OlderCursor : null;

    public StageOrderAnchor? NewerBoundary =>
        _isInitialized ? _segments[0].NewerBoundary : null;

    public StageOrderAnchor? OlderBoundary =>
        _isInitialized ? _segments[^1].OlderBoundary : null;

    public bool HasNewer => NewerCursor is not null;

    public bool HasOlder => OlderCursor is not null;

    public void EnsureMaximumSegmentCount(int minimumSegmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSegmentCount, 1);
        if (minimumSegmentCount <= _maximumSegmentCount)
        {
            return;
        }

        var maximumItemCount = checked(SegmentSize * minimumSegmentCount);
        _maximumSegmentCount = minimumSegmentCount;
        _maximumItemCount = maximumItemCount;
    }

    public StageRollingWindowChange Reset(StagePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var segment = ValidatePage(page);
        if (segment.NewerBoundary is not null)
        {
            throw new InvalidOperationException(
                "A Stage rolling-window reset must begin at the newest item.");
        }

        if (segment.OlderBoundary is not null && segment.Items.Count != SegmentSize)
        {
            throw new InvalidOperationException(
                "A Stage rolling-window reset with older items must fill its segment.");
        }

        var nextGeneration = checked(_generation + 1);
        var evicted = _items;
        _segments.Clear();
        _segments.Add(segment);
        _items = segment.Items;
        TotalCount = page.TotalCount;
        _generation = nextGeneration;
        _isInitialized = true;
        _requiresReset = false;

        return new StageRollingWindowChange(
            IsReset: true,
            AddedAt: null,
            AddedItems: segment.Items,
            EvictedFrom: null,
            EvictedItems: evicted);
    }

    public StageRollingWindowLoadRequest? CreateLoadRequest(
        StagePageDirection direction)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                "The Stage rolling window must be reset before loading an edge.");
        }

        if (_requiresReset)
        {
            return null;
        }

        var anchor = direction == StagePageDirection.TowardNewer
            ? NewerCursor
            : OlderCursor;
        if (anchor is null)
        {
            return null;
        }

        var edgeItem = direction == StagePageDirection.TowardNewer
            ? _items[0]
            : _items[^1];
        var expectedCursor = ToCursor(edgeItem);
        if (anchor.Value != expectedCursor)
        {
            throw new InvalidOperationException(
                "The Stage rolling-window edge cursor does not identify its edge item.");
        }

        return new StageRollingWindowLoadRequest(
            _ownerToken,
            _generation,
            direction,
            anchor.Value,
            ToOrderAnchor(edgeItem));
    }

    public void Invalidate()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                "The Stage rolling window must be initialized before it is invalidated.");
        }

        if (!_requiresReset)
        {
            InvalidateForReset();
        }
    }

    public StageRollingWindowApplyResult Apply(
        StageRollingWindowLoadRequest request,
        StagePage page)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(page);

        if (!_isInitialized ||
            _requiresReset ||
            request.OwnerToken != _ownerToken ||
            request.Generation != _generation ||
            !RequestStillOwnsEdge(request))
        {
            return new StageRollingWindowApplyResult(
                StageRollingWindowApplyStatus.Stale,
                Change: null);
        }

        if (IsEmptyDriftResponse(page))
        {
            InvalidateForReset();
            return new StageRollingWindowApplyResult(
                StageRollingWindowApplyStatus.ResetRequired,
                Change: null);
        }

        var segment = ValidatePage(page);
        if (page.TotalCount != TotalCount ||
            !JoinsCurrentWindow(request, segment) ||
            !StartsAtExpectedAdjacentItem(request, segment) ||
            ContainsRetainedId(segment))
        {
            InvalidateForReset();
            return new StageRollingWindowApplyResult(
                StageRollingWindowApplyStatus.ResetRequired,
                Change: null);
        }

        ValidateDirectionalFill(request.Direction, segment);

        var addedAt = request.Direction == StagePageDirection.TowardNewer
            ? StageRollingWindowEdge.Newer
            : StageRollingWindowEdge.Older;
        if (addedAt == StageRollingWindowEdge.Newer)
        {
            _segments.Insert(0, segment);
        }
        else
        {
            _segments.Add(segment);
        }

        StageRollingWindowEdge? evictedFrom = null;
        IReadOnlyList<StageItem> evictedItems = EmptyItems;
        if (_segments.Count > MaximumSegmentCount)
        {
            evictedFrom = addedAt == StageRollingWindowEdge.Newer
                ? StageRollingWindowEdge.Older
                : StageRollingWindowEdge.Newer;
            var evictedIndex = evictedFrom == StageRollingWindowEdge.Newer
                ? 0
                : _segments.Count - 1;
            evictedItems = _segments[evictedIndex].Items;
            _segments.RemoveAt(evictedIndex);
        }

        RebuildItems();
        return new StageRollingWindowApplyResult(
            StageRollingWindowApplyStatus.Applied,
            new StageRollingWindowChange(
                IsReset: false,
                AddedAt: addedAt,
                AddedItems: segment.Items,
                EvictedFrom: evictedFrom,
                EvictedItems: evictedItems));
    }

    public void ReconcileRetainedItems(
        IReadOnlyList<StageItem> retainedItems,
        StageOrderAnchor? newerBoundary,
        StageOrderAnchor? olderBoundary)
    {
        ArgumentNullException.ThrowIfNull(retainedItems);
        if (!_isInitialized || _requiresReset)
        {
            throw new InvalidOperationException(
                "The Stage rolling window must be valid before reconciling retained items.");
        }

        var snapshot = retainedItems.ToArray();
        if (snapshot.Length != _items.Count)
        {
            throw new InvalidOperationException(
                "A retained Stage reconciliation cannot change the window item count.");
        }

        var currentIds = _items.Select(static item => item.Id).ToHashSet();
        if (snapshot.Select(static item => item.Id).Distinct().Count() != snapshot.Length ||
            !currentIds.SetEquals(snapshot.Select(static item => item.Id)))
        {
            throw new InvalidOperationException(
                "A retained Stage reconciliation must preserve the window item identities.");
        }

        ValidateReconciledBoundary(
            NewerBoundary,
            newerBoundary,
            nameof(newerBoundary));
        ValidateReconciledBoundary(
            OlderBoundary,
            olderBoundary,
            nameof(olderBoundary));

        var segmentSizes = _segments
            .Select(static segment => segment.Items.Count)
            .ToArray();
        var replacement = BuildReconciledSegments(
            snapshot,
            segmentSizes,
            newerBoundary,
            olderBoundary);
        var nextGeneration = checked(_generation + 1);

        _segments.Clear();
        _segments.AddRange(replacement);
        _items = Array.AsReadOnly(snapshot);
        _generation = nextGeneration;
    }

    private static bool IsEmptyDriftResponse(StagePage page) =>
        page.Items is { Count: 0 } &&
        page.TotalCount >= 0 &&
        page.NewerCursor is null &&
        page.OlderCursor is null &&
        page.NewerBoundary is null &&
        page.OlderBoundary is null;

    private bool RequestStillOwnsEdge(StageRollingWindowLoadRequest request)
    {
        var currentCursor = request.Direction == StagePageDirection.TowardNewer
            ? NewerCursor
            : OlderCursor;
        if (currentCursor != request.Anchor || _items.Count == 0)
        {
            return false;
        }

        var currentItem = request.Direction == StagePageDirection.TowardNewer
            ? _items[0]
            : _items[^1];
        return ToOrderAnchor(currentItem) == request.JoinBoundary;
    }

    private static bool JoinsCurrentWindow(
        StageRollingWindowLoadRequest request,
        Segment segment) =>
        request.Direction switch
        {
            StagePageDirection.TowardOlder =>
                segment.NewerBoundary == request.JoinBoundary,
            StagePageDirection.TowardNewer =>
                segment.OlderBoundary == request.JoinBoundary,
            _ => false,
        };

    private bool StartsAtExpectedAdjacentItem(
        StageRollingWindowLoadRequest request,
        Segment segment)
    {
        if (segment.Items.Count == 0)
        {
            return false;
        }

        var expectedAdjacent = request.Direction == StagePageDirection.TowardOlder
            ? OlderBoundary
            : NewerBoundary;
        var loadedAdjacent = request.Direction == StagePageDirection.TowardOlder
            ? segment.Items[0]
            : segment.Items[^1];
        return expectedAdjacent == ToOrderAnchor(loadedAdjacent);
    }

    private bool ContainsRetainedId(Segment segment)
    {
        var retainedIds = _items.Select(static item => item.Id).ToHashSet();
        return segment.Items.Any(item => retainedIds.Contains(item.Id));
    }

    private IReadOnlyList<Segment> BuildReconciledSegments(
        IReadOnlyList<StageItem> items,
        IReadOnlyList<int> segmentSizes,
        StageOrderAnchor? newerBoundary,
        StageOrderAnchor? olderBoundary)
    {
        var replacements = new List<Segment>(segmentSizes.Count);
        var offset = 0;
        for (var index = 0; index < segmentSizes.Count; index++)
        {
            var segmentSize = segmentSizes[index];
            var segmentItems = items.Skip(offset).Take(segmentSize).ToArray();
            if (segmentItems.Length != segmentSize)
            {
                throw new InvalidOperationException(
                    "A retained Stage reconciliation did not preserve segment sizes.");
            }

            var segmentNewerBoundary = index == 0
                ? newerBoundary
                : ToOrderAnchor(items[offset - 1]);
            var segmentOlderBoundary = index == segmentSizes.Count - 1
                ? olderBoundary
                : ToOrderAnchor(items[offset + segmentSize]);
            StagePageCursor? segmentNewerCursor = segmentNewerBoundary is null
                ? null
                : ToCursor(segmentItems[0]);
            StagePageCursor? segmentOlderCursor = segmentOlderBoundary is null
                ? null
                : ToCursor(segmentItems[^1]);
            replacements.Add(ValidatePage(new StagePage(
                segmentItems,
                segmentNewerCursor,
                segmentOlderCursor,
                TotalCount,
                segmentNewerBoundary,
                segmentOlderBoundary)));
            offset += segmentSize;
        }

        if (offset != items.Count)
        {
            throw new InvalidOperationException(
                "A retained Stage reconciliation did not consume the full window.");
        }

        return replacements;
    }

    private static void ValidateReconciledBoundary(
        StageOrderAnchor? current,
        StageOrderAnchor? replacement,
        string parameterName)
    {
        if ((current is null) != (replacement is null) ||
            current?.Id != replacement?.Id)
        {
            throw new ArgumentException(
                "A retained Stage reconciliation must preserve boundary identities.",
                parameterName);
        }
    }

    private void ValidateDirectionalFill(
        StagePageDirection direction,
        Segment segment)
    {
        if (segment.Items.Count == 0)
        {
            throw new InvalidOperationException(
                "A Stage rolling-window edge load cannot return an empty segment.");
        }

        var hasMoreInLoadDirection = direction switch
        {
            StagePageDirection.TowardOlder => segment.OlderBoundary is not null,
            StagePageDirection.TowardNewer => segment.NewerBoundary is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        if (hasMoreInLoadDirection && segment.Items.Count != SegmentSize)
        {
            throw new InvalidOperationException(
                "A Stage rolling-window edge load with more items must fill its segment.");
        }
    }

    private Segment ValidatePage(StagePage page)
    {
        if (page.Items is null)
        {
            throw new InvalidOperationException("A Stage page must contain an item list.");
        }

        if (page.Items.Count > SegmentSize)
        {
            throw new InvalidOperationException(
                $"A Stage page cannot exceed the {SegmentSize}-item segment size.");
        }

        if (page.TotalCount < 0 || page.Items.Count > page.TotalCount)
        {
            throw new InvalidOperationException("A Stage page has an invalid total count.");
        }

        var snapshot = page.Items.ToArray();
        var ids = new HashSet<Guid>();
        for (var index = 0; index < snapshot.Length; index++)
        {
            var item = snapshot[index] ?? throw new InvalidOperationException(
                "A Stage page cannot contain a null item.");
            if (item.Id == Guid.Empty || item.Revision < 1 || !ids.Add(item.Id))
            {
                throw new InvalidOperationException(
                    "A Stage page contains an invalid or duplicate item identity.");
            }

            if (index > 0 &&
                ToOrderAnchor(snapshot[index - 1]).CompareTo(ToOrderAnchor(item)) <= 0)
            {
                throw new InvalidOperationException(
                    "A Stage page is not in strict descending composite-key order.");
            }
        }

        ValidateBoundary(
            page.NewerBoundary,
            page.NewerCursor,
            snapshot,
            isNewer: true);
        ValidateBoundary(
            page.OlderBoundary,
            page.OlderCursor,
            snapshot,
            isNewer: false);

        if (page.NewerBoundary is { } newer &&
            page.OlderBoundary is { } older &&
            newer.Id == older.Id)
        {
            throw new InvalidOperationException(
                "A Stage page cannot use the same item for both boundaries.");
        }

        var minimumTotal = snapshot.Length +
            (page.NewerBoundary is null ? 0 : 1) +
            (page.OlderBoundary is null ? 0 : 1);
        if (page.TotalCount < minimumTotal ||
            (page.NewerBoundary is null &&
             page.OlderBoundary is null &&
             page.TotalCount != snapshot.Length))
        {
            throw new InvalidOperationException(
                "A Stage page total is inconsistent with its visible boundaries.");
        }

        return new Segment(
            Array.AsReadOnly(snapshot),
            page.NewerCursor,
            page.OlderCursor,
            page.NewerBoundary,
            page.OlderBoundary);
    }

    private static void ValidateBoundary(
        StageOrderAnchor? boundary,
        StagePageCursor? cursor,
        IReadOnlyList<StageItem> items,
        bool isNewer)
    {
        if ((boundary is null) != (cursor is null))
        {
            throw new InvalidOperationException(
                "A Stage page boundary and continuation cursor must be present together.");
        }

        if (boundary is null)
        {
            return;
        }

        var value = boundary.Value;
        if (value.Id == Guid.Empty || value.Revision < 1 || items.Count == 0)
        {
            throw new InvalidOperationException("A Stage page boundary is invalid.");
        }

        var edgeItem = isNewer ? items[0] : items[^1];
        if (cursor != ToCursor(edgeItem) ||
            items.Any(item => item.Id == value.Id))
        {
            throw new InvalidOperationException(
                "A Stage page boundary does not match its loaded edge.");
        }

        var comparison = value.CompareTo(ToOrderAnchor(edgeItem));
        if ((isNewer && comparison <= 0) || (!isNewer && comparison >= 0))
        {
            throw new InvalidOperationException(
                "A Stage page boundary is outside the expected composite-key order.");
        }
    }

    private void InvalidateForReset()
    {
        _generation = checked(_generation + 1);
        _requiresReset = true;
    }

    private void RebuildItems()
    {
        var snapshot = new StageItem[_segments.Sum(static segment => segment.Items.Count)];
        var offset = 0;
        foreach (var segment in _segments)
        {
            for (var index = 0; index < segment.Items.Count; index++)
            {
                snapshot[offset++] = segment.Items[index];
            }
        }

        if (snapshot.Length > MaximumItemCount)
        {
            throw new InvalidOperationException(
                "The Stage rolling window exceeded its configured item bound.");
        }

        _items = Array.AsReadOnly(snapshot);
    }

    private static StagePageCursor ToCursor(StageItem item) =>
        new(item.SortOrder, item.Id);

    private static StageOrderAnchor ToOrderAnchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);

    private sealed record Segment(
        IReadOnlyList<StageItem> Items,
        StagePageCursor? NewerCursor,
        StagePageCursor? OlderCursor,
        StageOrderAnchor? NewerBoundary,
        StageOrderAnchor? OlderBoundary);
}
