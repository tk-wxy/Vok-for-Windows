namespace Vok.Features.Stage;

internal static class StageCardLayoutCalculator
{
    internal const StageCardLayoutTier DefaultTier =
        StageCardLayoutTier.ManagementSafe;
    internal const int MaximumColumnCount = 5;
    internal const double HorizontalGap = 8.0;
    internal const double VerticalGap = 8.0;
    internal const double MinimumSideInset = 4.0;

    internal static StageCardLayoutProfile Calculate(double viewportWidthDip) =>
        Calculate(viewportWidthDip, DefaultTier);

    internal static StageCardLayoutProfile Calculate(
        double viewportWidthDip,
        StageCardLayoutTier tier)
    {
        ValidatePositiveFinite(viewportWidthDip, nameof(viewportWidthDip));

        var dimensions = GetDimensions(tier);
        var columnCount = CalculateColumnCount(
            viewportWidthDip,
            dimensions.MinimumCardWidth);
        var cardWidth = CalculateCardWidth(
            viewportWidthDip,
            columnCount,
            dimensions.MaximumCardWidth);
        var occupiedWidth =
            (columnCount * cardWidth) + ((columnCount - 1) * HorizontalGap);
        var sideInset = Math.Max(0.0, (viewportWidthDip - occupiedWidth) / 2.0);

        return new StageCardLayoutProfile(
            tier,
            columnCount,
            cardWidth,
            dimensions.CardHeight,
            dimensions.CardHeight + VerticalGap,
            HorizontalGap,
            VerticalGap,
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

    private static StageCardDimensions GetDimensions(StageCardLayoutTier tier) =>
        tier switch
        {
            StageCardLayoutTier.Compact => new StageCardDimensions(
                MinimumCardWidth: 92.0,
                MaximumCardWidth: 104.0,
                CardHeight: 112.0),
            StageCardLayoutTier.ManagementSafe => new StageCardDimensions(
                MinimumCardWidth: 104.0,
                MaximumCardWidth: 124.0,
                CardHeight: 124.0),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
        };

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }
    }

    private readonly record struct StageCardDimensions(
        double MinimumCardWidth,
        double MaximumCardWidth,
        double CardHeight);
}
