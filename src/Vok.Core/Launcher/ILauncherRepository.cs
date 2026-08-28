namespace Vok.Core.Launcher;

public interface ILauncherRepository
{
    Task<LauncherPage> LoadPageAsync(
        long? afterSortOrder,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<LauncherAddResult> AddAsync(
        IReadOnlyList<LauncherTargetDescriptor> targets,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<bool> MoveToSlotAsync(
        Guid itemId,
        int slot,
        CancellationToken cancellationToken = default);
}
