namespace Vok.Features.Launcher;

internal enum LauncherReorderSessionState
{
    Previewing,
    Committing,
    Ended,
}

internal sealed class LauncherReorderSession(
    long generation,
    LauncherItemViewModel item,
    int originalIndex,
    IReadOnlyList<LauncherItemViewModel> originalOrder)
{
    public long Generation { get; } = generation;

    public LauncherItemViewModel Item { get; } =
        item ?? throw new ArgumentNullException(nameof(item));

    public int OriginalIndex { get; } = originalIndex;

    public int CurrentIndex { get; internal set; } = originalIndex;

    public IReadOnlyList<LauncherItemViewModel> OriginalOrder { get; } =
        originalOrder ?? throw new ArgumentNullException(nameof(originalOrder));

    public LauncherReorderSessionState State { get; private set; } =
        LauncherReorderSessionState.Previewing;

    public bool TryBeginCommit()
    {
        if (State is not LauncherReorderSessionState.Previewing)
        {
            return false;
        }

        State = LauncherReorderSessionState.Committing;
        return true;
    }

    public bool TryCancel()
    {
        if (State is not LauncherReorderSessionState.Previewing)
        {
            return false;
        }

        State = LauncherReorderSessionState.Ended;
        return true;
    }

    public void End() => State = LauncherReorderSessionState.Ended;
}
