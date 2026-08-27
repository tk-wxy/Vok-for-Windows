namespace Vok.Core.Platform;

public sealed class WorkspacePresentationFenceCompletedEventArgs(
    long generation,
    Exception? exception) : EventArgs
{
    public long Generation { get; } = generation;

    public Exception? Exception { get; } = exception;
}
