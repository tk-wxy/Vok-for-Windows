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
}
