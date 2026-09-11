namespace Vok.Core.Stage;

public enum StageReorderStatus
{
    Applied,
    NoChange,
    NotFound,
    RevisionConflict,
    NeighborConflict,
    SortOrderExhausted,
}

public sealed record StageReorderResult
{
    public StageReorderResult(
        StageReorderStatus status,
        IReadOnlyList<StageItem> changedItems)
    {
        ArgumentNullException.ThrowIfNull(changedItems);
        if (status is StageReorderStatus.Applied && changedItems.Count == 0)
        {
            throw new ArgumentException(
                "An applied Stage reorder must return authoritative changed items.",
                nameof(changedItems));
        }

        if (status is not StageReorderStatus.Applied && changedItems.Count != 0)
        {
            throw new ArgumentException(
                "A rejected or unchanged Stage reorder cannot return changed items.",
                nameof(changedItems));
        }

        Status = status;
        ChangedItems = Array.AsReadOnly(changedItems.ToArray());
    }

    public StageReorderStatus Status { get; }

    public IReadOnlyList<StageItem> ChangedItems { get; }
}
