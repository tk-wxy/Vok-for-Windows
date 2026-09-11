using System.Windows;

namespace Vok.Features.Stage;

internal readonly record struct StageReorderCardBounds(int Index, Rect Bounds);

internal readonly record struct StageReorderCardOffset(int Index, Vector Offset);

internal static class StageReorderGeometry
{
    internal static bool HasExceededDragThreshold(
        Vector delta,
        double horizontalThreshold,
        double verticalThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(horizontalThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(verticalThreshold, 0);
        return Math.Abs(delta.X) >= horizontalThreshold ||
            Math.Abs(delta.Y) >= verticalThreshold;
    }

    internal static int GetTargetIndex(
        Point pointer,
        IReadOnlyList<StageReorderCardBounds> realizedCards,
        int itemCount)
    {
        ArgumentNullException.ThrowIfNull(realizedCards);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);
        if (realizedCards.Count == 0)
        {
            throw new ArgumentException(
                "At least one realized Stage card is required.",
                nameof(realizedCards));
        }

        foreach (var card in realizedCards)
        {
            if (card.Index < 0 || card.Index >= itemCount ||
                card.Bounds.IsEmpty || card.Bounds.Width <= 0 || card.Bounds.Height <= 0)
            {
                throw new ArgumentException(
                    "A realized Stage card has invalid geometry.",
                    nameof(realizedCards));
            }
        }

        var rowAnchor = realizedCards
            .OrderBy(card => VerticalDistance(pointer.Y, card.Bounds))
            .ThenBy(card => Math.Abs(pointer.Y - CenterY(card.Bounds)))
            .ThenBy(static card => card.Index)
            .First();
        var rowTolerance = Math.Max(0.5, rowAnchor.Bounds.Height / 2);
        var row = realizedCards
            .Where(card => Math.Abs(CenterY(card.Bounds) - CenterY(rowAnchor.Bounds)) <
                rowTolerance)
            .OrderBy(static card => card.Bounds.Left)
            .ThenBy(static card => card.Index)
            .ToArray();

        foreach (var card in row)
        {
            if (pointer.X < card.Bounds.Left + (card.Bounds.Width / 2))
            {
                return card.Index;
            }
        }

        return row[^1].Index;
    }

    internal static double GetAutoScrollVelocity(
        double pointerY,
        double viewportHeight,
        double edgeZone,
        double maximumSpeed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(viewportHeight, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(edgeZone, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSpeed, 0);
        if (pointerY < edgeZone)
        {
            return -maximumSpeed * Math.Clamp((edgeZone - pointerY) / edgeZone, 0, 1);
        }

        if (pointerY > viewportHeight - edgeZone)
        {
            return maximumSpeed * Math.Clamp(
                (pointerY - (viewportHeight - edgeZone)) / edgeZone,
                0,
                1);
        }

        return 0;
    }

    internal static IReadOnlyList<StageReorderCardOffset> GetPreviewOffsets(
        IReadOnlyList<StageReorderCardBounds> realizedCards,
        int sourceIndex,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(realizedCards);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        if (sourceIndex == targetIndex || realizedCards.Count == 0)
        {
            return [];
        }

        var slots = realizedCards.ToDictionary(static card => card.Index);
        var offsets = new List<StageReorderCardOffset>();
        foreach (var card in realizedCards)
        {
            var destinationIndex = sourceIndex < targetIndex
                ? card.Index > sourceIndex && card.Index <= targetIndex
                    ? card.Index - 1
                    : card.Index
                : card.Index >= targetIndex && card.Index < sourceIndex
                    ? card.Index + 1
                    : card.Index;
            if (destinationIndex == card.Index ||
                !slots.TryGetValue(destinationIndex, out var destination))
            {
                continue;
            }

            offsets.Add(new StageReorderCardOffset(
                card.Index,
                destination.Bounds.TopLeft - card.Bounds.TopLeft));
        }

        return offsets;
    }

    private static double VerticalDistance(double y, Rect bounds)
    {
        if (y < bounds.Top)
        {
            return bounds.Top - y;
        }

        return y > bounds.Bottom ? y - bounds.Bottom : 0;
    }

    private static double CenterY(Rect bounds) => bounds.Top + (bounds.Height / 2);
}
