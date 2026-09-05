namespace Vok.Core.Stage;

public enum StageRelinkStatus
{
    Applied,
    NoChange,
    NotFound,
    RevisionConflict,
    CanonicalConflict,
    KindMismatch,
}

public sealed record StageRelinkResult(
    StageRelinkStatus Status,
    StageItem? Item);
