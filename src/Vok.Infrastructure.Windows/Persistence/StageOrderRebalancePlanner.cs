using Vok.Core.Stage;

namespace Vok.Infrastructure.Windows.Persistence;

internal readonly record struct StageOrderRebalanceAssignment(
    StageItem Item,
    long SortOrder);

internal sealed class StageOrderRebalancePlan
{
    public StageOrderRebalancePlan(
        IReadOnlyList<StageOrderRebalanceAssignment> assignments,
        long step,
        int newerItemCount)
    {
        Assignments = Array.AsReadOnly(assignments.ToArray());
        Step = step;
        NewerItemCount = newerItemCount;
        ChangedCount = Assignments.Count(
            static assignment => assignment.Item.SortOrder != assignment.SortOrder);
    }

    public IReadOnlyList<StageOrderRebalanceAssignment> Assignments { get; }

    public long Step { get; }

    public int NewerItemCount { get; }

    public int ChangedCount { get; }
}

internal static class StageOrderRebalancePlanner
{
    public static StageOrderRebalancePlan? TryCreate(
        StageItem source,
        IReadOnlyList<StageItem> newerCandidates,
        IReadOnlyList<StageItem> olderCandidates,
        long preferredStep)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(newerCandidates);
        ArgumentNullException.ThrowIfNull(olderCandidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredStep, 1);

        var newerAvailable = Math.Min(
            newerCandidates.Count,
            StageReorderResult.MaximumChangedItems);
        var olderAvailable = Math.Min(
            olderCandidates.Count,
            StageReorderResult.MaximumChangedItems);
        var maximumWindowSize = Math.Min(
            StageReorderResult.MaximumChangedItems,
            checked(1 + newerAvailable + olderAvailable));

        for (var windowSize = 2; windowSize <= maximumWindowSize; windowSize++)
        {
            StageOrderRebalancePlan? best = null;
            var surroundingItemCount = windowSize - 1;
            var minimumNewerCount = Math.Max(
                0,
                surroundingItemCount - olderAvailable);
            var maximumNewerCount = Math.Min(
                surroundingItemCount,
                newerAvailable);

            for (var newerCount = minimumNewerCount;
                 newerCount <= maximumNewerCount;
                 newerCount++)
            {
                var olderCount = surroundingItemCount - newerCount;
                var windowItems = BuildWindow(
                    source,
                    newerCandidates,
                    newerCount,
                    olderCandidates,
                    olderCount);
                var externalNewer = newerCount < newerAvailable
                    ? newerCandidates[newerCount]
                    : null;
                var externalOlder = olderCount < olderAvailable
                    ? olderCandidates[olderCount]
                    : null;
                if (!TryAssignSortOrders(
                        windowItems.Count,
                        externalNewer?.SortOrder,
                        externalOlder?.SortOrder,
                        preferredStep,
                        out var sortOrders,
                        out var step))
                {
                    continue;
                }

                var assignments = new StageOrderRebalanceAssignment[windowSize];
                for (var index = 0; index < windowSize; index++)
                {
                    assignments[index] = new StageOrderRebalanceAssignment(
                        windowItems[index],
                        sortOrders[index]);
                }

                var candidate = new StageOrderRebalancePlan(
                    assignments,
                    step,
                    newerCount);
                if (candidate.ChangedCount == 0)
                {
                    continue;
                }

                if (IsBetterCandidate(candidate, best, olderCount))
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private static List<StageItem> BuildWindow(
        StageItem source,
        IReadOnlyList<StageItem> newerCandidates,
        int newerCount,
        IReadOnlyList<StageItem> olderCandidates,
        int olderCount)
    {
        var items = new List<StageItem>(1 + newerCount + olderCount);
        for (var index = newerCount - 1; index >= 0; index--)
        {
            items.Add(newerCandidates[index]);
        }

        items.Add(source);
        for (var index = 0; index < olderCount; index++)
        {
            items.Add(olderCandidates[index]);
        }

        return items;
    }

    private static bool TryAssignSortOrders(
        int itemCount,
        long? externalNewer,
        long? externalOlder,
        long preferredStep,
        out long[] sortOrders,
        out long step)
    {
        Int128 maximumStep;
        Int128 first;
        if (externalNewer is { } newer && externalOlder is { } older)
        {
            var available = (Int128)newer - older;
            maximumStep = available / (itemCount + 1);
            if (maximumStep < 1)
            {
                return Fail(out sortOrders, out step);
            }

            maximumStep = Int128.Min(maximumStep, preferredStep);
            first = (Int128)newer - maximumStep;
        }
        else if (externalNewer is { } onlyNewer)
        {
            var available = (Int128)onlyNewer - long.MinValue;
            maximumStep = available / itemCount;
            if (maximumStep < 1)
            {
                return Fail(out sortOrders, out step);
            }

            maximumStep = Int128.Min(maximumStep, preferredStep);
            first = (Int128)onlyNewer - maximumStep;
        }
        else if (externalOlder is { } onlyOlder)
        {
            var available = (Int128)long.MaxValue - onlyOlder;
            maximumStep = available / itemCount;
            if (maximumStep < 1)
            {
                return Fail(out sortOrders, out step);
            }

            maximumStep = Int128.Min(maximumStep, preferredStep);
            first = (Int128)onlyOlder + (maximumStep * itemCount);
        }
        else
        {
            maximumStep = preferredStep;
            var span = maximumStep * (itemCount - 1);
            first = span / 2;
        }

        sortOrders = new long[itemCount];
        for (var index = 0; index < itemCount; index++)
        {
            var value = first - (maximumStep * index);
            if (value < long.MinValue || value > long.MaxValue)
            {
                return Fail(out sortOrders, out step);
            }

            sortOrders[index] = (long)value;
        }

        step = (long)maximumStep;
        return true;
    }

    private static bool IsBetterCandidate(
        StageOrderRebalancePlan candidate,
        StageOrderRebalancePlan? current,
        int candidateOlderCount)
    {
        if (current is null)
        {
            return true;
        }

        if (candidate.Step != current.Step)
        {
            return candidate.Step > current.Step;
        }

        if (candidate.ChangedCount != current.ChangedCount)
        {
            return candidate.ChangedCount < current.ChangedCount;
        }

        var candidateImbalance = Math.Abs(
            candidate.NewerItemCount - candidateOlderCount);
        var currentOlderCount = current.Assignments.Count - 1 -
            current.NewerItemCount;
        var currentImbalance = Math.Abs(
            current.NewerItemCount - currentOlderCount);
        if (candidateImbalance != currentImbalance)
        {
            return candidateImbalance < currentImbalance;
        }

        return candidate.NewerItemCount < current.NewerItemCount;
    }

    private static bool Fail(out long[] sortOrders, out long step)
    {
        sortOrders = [];
        step = default;
        return false;
    }
}
