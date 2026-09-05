using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Hosting;

public interface IWorkspaceCommands
{
    event EventHandler? ShutdownRequested;

    Task StartAsync();

    Task<bool> HideForLauncherActionAsync(CancellationToken cancellationToken = default);

    Task<bool> HideForStageManagementActionAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<WorkspaceActionHideResult> HideForPasteActionAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WorkspaceActionHideResult.NotHidden);

    Task<WorkspacePasteTargetClaim?> AcquirePasteTargetClaimAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<WorkspacePasteTargetClaim?>(null);

    Task<WorkspaceActionHideResult> HideAfterImageFileCreatedAsync(
        WorkspacePasteTargetClaim claim,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WorkspaceActionHideResult.NotHidden);

    void EnsureVisible(
        ForegroundTarget foregroundTarget,
        long requestedTimestamp,
        WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent);

    void RequestExit();

    void ReportFault(Exception exception);
}
