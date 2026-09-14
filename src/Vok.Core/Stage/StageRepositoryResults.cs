namespace Vok.Core.Stage;

public enum StageAddBatchStatus
{
    Applied,
    CapacityExceeded,
    SortOrderExhausted,
}

public sealed record StageAddBatchResult(
    StageAddBatchStatus Status,
    long CountBefore,
    long CountAfter,
    long AddedCount,
    long Capacity);
