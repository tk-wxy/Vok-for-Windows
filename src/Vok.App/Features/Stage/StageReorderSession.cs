using Vok.Core.Stage;

namespace Vok.Features.Stage;

internal enum StageReorderSessionState
{
    Previewing,
    Committing,
    Ended,
}

internal sealed class StageReorderSession
{
    internal StageReorderSession(
        long generation,
        StageItemViewModel source,
        IReadOnlyList<StageItemViewModel> originalItems,
        int originalIndex,
        StageOrderAnchor? newerBoundary,
        StageOrderAnchor? olderBoundary)
    {
        Generation = generation;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        OriginalItems = originalItems?.ToArray() ??
            throw new ArgumentNullException(nameof(originalItems));
        if (originalIndex < 0 || originalIndex >= OriginalItems.Count ||
            !ReferenceEquals(OriginalItems[originalIndex], source))
        {
            throw new ArgumentOutOfRangeException(nameof(originalIndex));
        }

        OriginalIndex = originalIndex;
        CurrentIndex = originalIndex;
        SourceVersion = new StageItemVersion(source.Id, source.Revision);
        NewerBoundary = newerBoundary;
        OlderBoundary = olderBoundary;
    }

    internal long Generation { get; }

    internal StageItemViewModel Source { get; }

    internal IReadOnlyList<StageItemViewModel> OriginalItems { get; }

    internal int OriginalIndex { get; }

    internal int CurrentIndex { get; set; }

    internal StageItemVersion SourceVersion { get; }

    internal StageOrderAnchor? NewerBoundary { get; }

    internal StageOrderAnchor? OlderBoundary { get; }

    internal StageReorderSessionState State { get; private set; } =
        StageReorderSessionState.Previewing;

    internal bool TryBeginCommit()
    {
        if (State is not StageReorderSessionState.Previewing)
        {
            return false;
        }

        State = StageReorderSessionState.Committing;
        return true;
    }

    internal bool TryEnd()
    {
        if (State is StageReorderSessionState.Ended)
        {
            return false;
        }

        State = StageReorderSessionState.Ended;
        return true;
    }
}
