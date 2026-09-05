namespace Vok.Core.Stage;

public enum StageRemoveStatus
{
    Removed,
    NotFound,
    RevisionConflict,
}

public readonly record struct StageRemoveResult(StageRemoveStatus Status);
