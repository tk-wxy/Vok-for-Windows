namespace Vok.Features.Stage;

internal static class StageCardLayoutCalculator
{
    internal const int MaximumColumnCount = 10;
    internal const double HorizontalGap = 6.0;
    internal const double MinimumSideInset = 4.0;
    internal const double MinimumCardWidth = 72.0;
    internal const double MaximumCardWidth = 72.0;
    internal const double DefaultCardHeight = 94.0;
    internal const double DefaultRowUnitHeight = 100.0;

    internal static StageCardLayoutProfile Calculate(double viewportWidthDip)
    {
        ValidatePositiveFinite(viewportWidthDip, nameof(viewportWidthDip));

        var columnCount = CalculateColumnCount(
            viewportWidthDip,
            MinimumCardWidth);
        var cardWidth = CalculateCardWidth(
            viewportWidthDip,
            columnCount,
            MaximumCardWidth);
        var occupiedWidth =
            (columnCount * cardWidth) + ((columnCount - 1) * HorizontalGap);
        var sideInset = Math.Max(0.0, (viewportWidthDip - occupiedWidth) / 2.0);

        return new StageCardLayoutProfile(
            columnCount,
            cardWidth,
            HorizontalGap,
            sideInset);
    }

    private static int CalculateColumnCount(
        double viewportWidthDip,
        double minimumCardWidth)
    {
        if (viewportWidthDip < minimumCardWidth + (2.0 * MinimumSideInset))
        {
            return 1;
        }

        var availableWidth = viewportWidthDip - (2.0 * MinimumSideInset);
        var unboundedColumns = Math.Floor(
            (availableWidth + HorizontalGap) /
            (minimumCardWidth + HorizontalGap));

        return unboundedColumns >= MaximumColumnCount
            ? MaximumColumnCount
            : Math.Max(1, (int)unboundedColumns);
    }

    private static double CalculateCardWidth(
        double viewportWidthDip,
        int columnCount,
        double maximumCardWidth)
    {
        var gapWidth = (columnCount - 1) * HorizontalGap;
        var availableWidth =
            viewportWidthDip - gapWidth - (2.0 * MinimumSideInset);

        return availableWidth > 0
            ? Math.Min(maximumCardWidth, availableWidth / columnCount)
            : viewportWidthDip / columnCount;
    }

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }
    }
}
