namespace Vok.Core.Stage;

public enum StageItemMutationStatus
{
    Applied,
    NoChange,
    NotFound,
    RevisionConflict,
}

public sealed record StageItemMutationResult(
    StageItemMutationStatus Status,
    StageItem? Item);
