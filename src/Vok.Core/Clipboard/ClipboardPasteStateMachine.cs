namespace Vok.Core.Clipboard;

public enum ClipboardPasteState
{
    Idle = 0,
    Materializing = 1,
    WritingClipboard = 2,
    HidingWorkspace = 3,
    VerifyingCurrent = 4,
    InjectingPaste = 5,
    Completed = 6,
    CopiedOnly = 7,
    Failed = 8,
    Cancelled = 9,
}
public readonly record struct ClipboardPasteTransition(
    bool Accepted,
    long Generation,
    ClipboardPasteState State);

public sealed class ClipboardPasteStateMachine
{
    private long _nextGeneration;

    public ClipboardPasteState State { get; private set; } = ClipboardPasteState.Idle;

    public long ActiveGeneration { get; private set; }

    public Guid ActiveEntryId { get; private set; }

    public ClipboardPasteTransition Begin(Guid entryId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        if (State is not (ClipboardPasteState.Idle or
            ClipboardPasteState.Completed or
            ClipboardPasteState.CopiedOnly or
            ClipboardPasteState.Failed or
            ClipboardPasteState.Cancelled))
        {
            return new ClipboardPasteTransition(false, ActiveGeneration, State);
        }

        ActiveGeneration = checked(++_nextGeneration);
        ActiveEntryId = entryId;
        State = ClipboardPasteState.Materializing;
        return new ClipboardPasteTransition(true, ActiveGeneration, State);
    }

    public bool Advance(
        long generation,
        ClipboardPasteState expected,
        ClipboardPasteState next)
    {
        if (generation != ActiveGeneration || State != expected || !CanAdvance(expected, next))
        {
            return false;
        }

        State = next;
        return true;
    }

    public bool Finish(long generation, ClipboardPasteState terminal)
    {
        if (generation != ActiveGeneration || !IsTerminal(terminal) || IsTerminal(State))
        {
            return false;
        }

        State = terminal;
        ActiveEntryId = Guid.Empty;
        return true;
    }

    public bool Cancel(long generation) => Finish(generation, ClipboardPasteState.Cancelled);

    private static bool CanAdvance(ClipboardPasteState current, ClipboardPasteState next) =>
        (current, next) is
            (ClipboardPasteState.Materializing, ClipboardPasteState.WritingClipboard) or
            (ClipboardPasteState.WritingClipboard, ClipboardPasteState.HidingWorkspace) or
            (ClipboardPasteState.HidingWorkspace, ClipboardPasteState.VerifyingCurrent) or
            (ClipboardPasteState.VerifyingCurrent, ClipboardPasteState.InjectingPaste);

    private static bool IsTerminal(ClipboardPasteState state) => state is
        ClipboardPasteState.Completed or
        ClipboardPasteState.CopiedOnly or
        ClipboardPasteState.Failed or
        ClipboardPasteState.Cancelled;
}
