namespace Vok.Core.Workspace;

public sealed record WorkspaceSession(
    long Generation,
    ForegroundTarget Target,
    long RequestedTimestamp);
