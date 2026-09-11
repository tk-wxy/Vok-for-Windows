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
    public const int MaximumChangedItems = 64;

    public StageReorderResult(
        StageReorderStatus status,
        IReadOnlyList<StageItem> changedItems)
    {
        ArgumentNullException.ThrowIfNull(changedItems);
        var snapshot = changedItems.ToArray();
        if (status is StageReorderStatus.Applied && snapshot.Length == 0)
        {
            throw new ArgumentException(
                "An applied Stage reorder must return authoritative changed items.",
                nameof(changedItems));
        }

        if (status is not StageReorderStatus.Applied && snapshot.Length != 0)
        {
            throw new ArgumentException(
                "A rejected or unchanged Stage reorder cannot return changed items.",
                nameof(changedItems));
        }

        if (snapshot.Length > MaximumChangedItems)
        {
            throw new ArgumentException(
                $"A Stage reorder cannot change more than {MaximumChangedItems} items.",
                nameof(changedItems));
        }

        if (snapshot.Select(static item => item.Id).Distinct().Count() !=
            snapshot.Length)
        {
            throw new ArgumentException(
                "A Stage reorder cannot return the same changed item more than once.",
                nameof(changedItems));
        }

        Status = status;
        ChangedItems = Array.AsReadOnly(snapshot);
    }

    public StageReorderStatus Status { get; }

    public IReadOnlyList<StageItem> ChangedItems { get; }
}
