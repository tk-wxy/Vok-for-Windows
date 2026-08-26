using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Hosting;

public interface IWorkspaceCommands
{
    event EventHandler? ShutdownRequested;

    Task StartAsync();

    void EnsureVisible(
        ForegroundTarget foregroundTarget,
        long requestedTimestamp,
        WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent);

    void RequestExit();

    void ReportFault(Exception exception);
}
