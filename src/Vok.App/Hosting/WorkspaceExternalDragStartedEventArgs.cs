namespace Vok.Hosting;

public sealed class WorkspaceExternalDragStartedEventArgs(long dragId) : EventArgs
{
    public long DragId { get; } = dragId > 0
        ? dragId
        : throw new ArgumentOutOfRangeException(nameof(dragId));
}
