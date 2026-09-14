namespace Vok.Core.Stage;

public readonly record struct StagePageCursor(
    long SortOrder,
    Guid Id);

public enum StagePageDirection
{
    TowardOlder = 0,
    TowardNewer = 1,
}

public sealed record StagePage(
    IReadOnlyList<StageItem> Items,
    StagePageCursor? NewerCursor,
    StagePageCursor? OlderCursor,
    long TotalCount,
    StageOrderAnchor? NewerBoundary = null,
    StageOrderAnchor? OlderBoundary = null)
{
    public bool HasNewer => NewerCursor is not null;

    public bool HasOlder => OlderCursor is not null;
}
