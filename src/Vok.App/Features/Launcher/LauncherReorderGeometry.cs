namespace Vok.Features.Launcher;

internal static class LauncherReorderGeometry
{
    internal const int ColumnCount = 4;

    public static int GetTargetIndex(
        double pointerX,
        double pointerY,
        double usableWidth,
        double verticalOffset,
        double cardUnitHeight,
        int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(usableWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cardUnitHeight, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);

        var columnWidth = usableWidth / ColumnCount;
        var x = Math.Clamp(pointerX, 0, Math.Max(0, usableWidth - 0.001));
        var column = Math.Clamp((int)(x / columnWidth), 0, ColumnCount - 1);
        var row = Math.Max(
            0,
            (int)Math.Floor((pointerY + Math.Max(0, verticalOffset)) / cardUnitHeight));
        return Math.Clamp(
            checked(row * ColumnCount + column),
            0,
            itemCount - 1);
    }

    public static double GetAutoScrollVelocity(
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
}
