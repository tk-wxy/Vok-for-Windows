namespace Vok.Hosting;

internal readonly record struct WorkspaceRevealReadinessToken(
    long Generation,
    long ContentVersion,
    long LayoutVersion,
    long StateVersion);

internal sealed class WorkspaceRevealReadyEventArgs(
    WorkspaceRevealReadinessToken token) : EventArgs
{
    public WorkspaceRevealReadinessToken Token { get; } = token;
}

/// <summary>
/// Proves that the reusable window's generation-specific static content is complete.
/// The host still owns the subsequent WPF render-return and DWM presentation fence.
/// </summary>
internal interface IWorkspaceRevealReadiness
{
    event EventHandler<WorkspaceRevealReadyEventArgs>? RevealReady;

    void BeginReveal(long generation);

    bool IsRevealReady(WorkspaceRevealReadinessToken token);

    void CompleteReveal(long generation);

    void CancelReveal(long generation);
}
