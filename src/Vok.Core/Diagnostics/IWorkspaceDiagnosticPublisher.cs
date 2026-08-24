namespace Vok.Core.Diagnostics;

public interface IWorkspaceDiagnosticPublisher
{
    void Publish(nint workspaceWindow, WorkspaceDiagnosticSnapshot snapshot);

    void Clear(nint workspaceWindow);
}
