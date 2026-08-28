namespace Vok.Features.Launcher;

internal sealed class LauncherLaunchFeedbackGate
{
    private long _nextGeneration;
    private long _activeGeneration;

    public bool HasActive => Volatile.Read(ref _activeGeneration) != 0;

    public long ActiveGeneration => Volatile.Read(ref _activeGeneration);

    public long Begin()
    {
        var generation = Interlocked.Increment(ref _nextGeneration);
        Volatile.Write(ref _activeGeneration, generation);
        return generation;
    }

    public bool TryClaim(long generation) =>
        generation != 0 &&
        Interlocked.CompareExchange(ref _activeGeneration, 0, generation) == generation;

    public void Cancel() => Interlocked.Exchange(ref _activeGeneration, 0);
}
