namespace Vok.Core.Platform;

public sealed class WorkspacePresentationFenceCompletedEventArgs(
    long generation,
    long attempt,
    Exception? exception) : EventArgs
{
    public long Generation { get; } = generation;

    public long Attempt { get; } = attempt;

    public Exception? Exception { get; } = exception;
}
