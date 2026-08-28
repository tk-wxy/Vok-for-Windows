namespace Vok.Hosting;

public sealed class WorkspaceExternalDragEndedEventArgs(
    long dragId,
    bool dropAccepted) : EventArgs
{
    public long DragId { get; } = dragId > 0
        ? dragId
        : throw new ArgumentOutOfRangeException(nameof(dragId));

    public bool DropAccepted { get; } = dropAccepted;
}
