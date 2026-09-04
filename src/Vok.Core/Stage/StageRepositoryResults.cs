namespace Vok.Core.Stage;

public enum StageAddOrMoveStatus
{
    Applied,
    CapacityExceeded,
    SortOrderExhausted,
}

public sealed record StageAddOrMoveResult(
    StageAddOrMoveStatus Status,
    long CountBefore,
    long CountAfter,
    long AddedCount,
    long MovedCount,
    long Capacity);
