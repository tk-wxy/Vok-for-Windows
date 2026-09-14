namespace Vok.Hosting;

/// <summary>Observes content at host checkpoints without participating in the reveal decision.</summary>
internal interface IWorkspaceRevealDiagnostics
{
    void ObserveReveal(string phase, long generation);
}
