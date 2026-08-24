namespace Vok.Core.Workspace;

/// <summary>
/// Owns a single workspace visibility session. All calls must be serialized by the UI dispatcher.
/// Generation checks prevent delayed callbacks from completing a newer session.
/// </summary>
public sealed class WorkspaceSessionStateMachine
{
    private long _nextGeneration;

    public WorkspaceState State { get; private set; } = WorkspaceState.Hidden;

    public WorkspaceSession? Current { get; private set; }

    public WorkspaceTransition BeginShow(ForegroundTarget target, long requestedTimestamp)
    {
        if (State is not WorkspaceState.Hidden)
        {
            return WorkspaceTransition.Rejected(State, Current?.Generation ?? 0);
        }

        var generation = checked(++_nextGeneration);
        Current = new WorkspaceSession(generation, target, requestedTimestamp);
        State = WorkspaceState.Revealing;
        return WorkspaceTransition.Applied(State, generation);
    }

    public bool CompleteShow(long generation, WorkspacePresentationStatus presentation)
    {
        if (!IsCurrent(generation) ||
            State is not WorkspaceState.Revealing ||
            !presentation.IsInputReady)
        {
            return false;
        }

        State = WorkspaceState.Visible;
        return true;
    }

    public WorkspaceTransition BeginHide(WorkspaceHideReason reason)
    {
        _ = reason;

        if (Current is null || State is WorkspaceState.Hidden or WorkspaceState.Hiding)
        {
            return WorkspaceTransition.Rejected(State, Current?.Generation ?? 0);
        }

        State = WorkspaceState.Hiding;
        return WorkspaceTransition.Applied(State, Current.Generation);
    }

    public bool CompleteHide(long generation)
    {
        if (!IsCurrent(generation) || State is not WorkspaceState.Hiding)
        {
            return false;
        }

        State = WorkspaceState.Hidden;
        Current = null;
        return true;
    }

    public bool FailShow(long generation)
    {
        if (!IsCurrent(generation) || State is not (WorkspaceState.Revealing or WorkspaceState.Visible))
        {
            return false;
        }

        State = WorkspaceState.Hidden;
        Current = null;
        return true;
    }

    public bool RollBackHide(long generation)
    {
        if (!IsCurrent(generation) || State is not WorkspaceState.Hiding)
        {
            return false;
        }

        State = WorkspaceState.Visible;
        return true;
    }

    public bool IsCurrentVisible(long generation) =>
        IsCurrent(generation) && State is WorkspaceState.Visible;

    public bool IsCurrentSession(long generation) => IsCurrent(generation);

    private bool IsCurrent(long generation) =>
        Current is { Generation: var currentGeneration } && currentGeneration == generation;
}
