namespace Vok.Core.Workspace;

public readonly record struct WorkspaceTransition(
    bool Accepted,
    long Generation,
    WorkspaceState State)
{
    public static WorkspaceTransition Rejected(WorkspaceState state, long generation = 0) =>
        new(false, generation, state);

    public static WorkspaceTransition Applied(WorkspaceState state, long generation) =>
        new(true, generation, state);
}
