namespace Vok.Core.Stage;

public readonly record struct StagePageCursor(
    long SortOrder,
    Guid Id);

public sealed record StagePage(
    IReadOnlyList<StageItem> Items,
    StagePageCursor? NextCursor,
    long TotalCount);
