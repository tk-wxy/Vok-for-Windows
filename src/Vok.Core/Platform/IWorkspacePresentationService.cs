namespace Vok.Core.Platform;

/// <summary>
/// Owns the DWM visibility barrier for the reusable workspace HWND.
/// Fence completions may be raised from a background thread.
/// </summary>
public interface IWorkspacePresentationService : IDisposable
{
    event EventHandler<WorkspacePresentationFenceCompletedEventArgs>? FenceCompleted;

    void SetCloaked(nint workspaceWindow, bool cloaked);

    bool IsCloaked(nint workspaceWindow);

    bool TryRequestFence(long generation);
}
