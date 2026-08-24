using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public interface IWindowPlacementService
{
    void ApplyFullScreen(nint workspaceWindow, ForegroundTarget target);

    bool RequestForeground(nint workspaceWindow);

    WorkspacePresentationObservation Observe(nint workspaceWindow);

    void ExitTopmost(nint workspaceWindow);
}
