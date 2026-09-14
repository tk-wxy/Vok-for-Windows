namespace Vok.Core.Stage;

public enum StageRelinkStatus
{
    Applied,
    NoChange,
    NotFound,
    RevisionConflict,
    KindMismatch,
}

public sealed record StageRelinkResult(
    StageRelinkStatus Status,
    StageItem? Item);
