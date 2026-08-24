using Vok.Core.Workspace;

namespace Vok.Hosting;

public sealed class WorkspaceHideRequestedEventArgs(WorkspaceHideReason reason) : EventArgs
{
    public WorkspaceHideReason Reason { get; } = reason;
}
