using Vok.Core.Stage;

namespace Vok.StageScaleProbe;

internal sealed class InspectionReadOnlyStageRepository(IStageRepository inner)
    : IStageRepository
{
    internal const string RejectionMessage =
        "The physical inspection repository is read-only.";

    public Task<StagePage> LoadPageAsync(
        StagePageCursor? anchor,
        StagePageDirection direction,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        inner.LoadPageAsync(anchor, direction, pageSize, cancellationToken);

    public Task<StageAddBatchResult> AddBatchAsync(
        IReadOnlyList<StageTargetDescriptor> targets,
        CancellationToken cancellationToken = default) =>
        Reject<StageAddBatchResult>();

    public Task<StageRemoveResult> RemoveAsync(
        Guid itemId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        Reject<StageRemoveResult>();

    public Task<StageItemMutationResult> SetPinnedAsync(
        Guid itemId,
        long expectedRevision,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        Reject<StageItemMutationResult>();

    public Task<StageRelinkResult> RelinkAsync(
        Guid itemId,
        long expectedRevision,
        StageTargetDescriptor target,
        CancellationToken cancellationToken = default) =>
        Reject<StageRelinkResult>();

    public Task<StageReorderResult> ReorderAsync(
        StageReorderRequest request,
        CancellationToken cancellationToken = default) =>
        Reject<StageReorderResult>();

    private static Task<T> Reject<T>() =>
        Task.FromException<T>(new InvalidOperationException(RejectionMessage));
}
