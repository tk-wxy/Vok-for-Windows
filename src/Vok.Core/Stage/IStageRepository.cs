namespace Vok.Core.Stage;

public interface IStageRepository
{
    Task<StagePage> LoadPageAsync(
        StagePageCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<StageAddOrMoveResult> AddOrMoveAsync(
        IReadOnlyList<StageTargetDescriptor> targets,
        CancellationToken cancellationToken = default);

    Task<StageRemoveResult> RemoveAsync(
        Guid itemId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<StageItemMutationResult> SetPinnedAsync(
        Guid itemId,
        long expectedRevision,
        bool isPinned,
        CancellationToken cancellationToken = default);

    Task<StageRelinkResult> RelinkAsync(
        Guid itemId,
        long expectedRevision,
        StageTargetDescriptor target,
        CancellationToken cancellationToken = default);
}
