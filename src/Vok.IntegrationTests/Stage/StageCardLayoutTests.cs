using Vok.Features.Stage;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageCardLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void CalculateRejectsNonPositiveOrNonFiniteWidths(double viewportWidthDip)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StageCardLayoutCalculator.Calculate(viewportWidthDip));
    }

    [Fact]
    public void CalculateRejectsUnknownTier()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StageCardLayoutCalculator.Calculate(
                520,
                (StageCardLayoutTier)int.MaxValue));
    }

    [Theory]
    [InlineData((int)StageCardLayoutTier.Compact, 260, 2, 104, 112, 22)]
    [InlineData((int)StageCardLayoutTier.Compact, 520, 5, 96, 112, 4)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 260, 2, 122, 124, 4)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 520, 4, 122, 124, 4)]
    public void CandidateProfilesCoverSupportedStageWidths(
        int tierValue,
        double viewportWidthDip,
        int expectedColumns,
        double expectedCardWidth,
        double expectedCardHeight,
        double expectedSideInset)
    {
        var tier = (StageCardLayoutTier)tierValue;
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip, tier);

        Assert.Equal(tier, profile.Tier);
        Assert.Equal(expectedColumns, profile.ColumnCount);
        Assert.Equal(expectedCardWidth, profile.CardWidth, precision: 8);
        Assert.Equal(expectedCardHeight, profile.CardHeight, precision: 8);
        Assert.Equal(
            expectedCardHeight + StageCardLayoutCalculator.VerticalGap,
            profile.RowUnitHeight,
            precision: 8);
        Assert.Equal(
            StageCardLayoutCalculator.HorizontalGap,
            profile.HorizontalGap,
            precision: 8);
        Assert.Equal(
            StageCardLayoutCalculator.VerticalGap,
            profile.VerticalGap,
            precision: 8);
        Assert.Equal(expectedSideInset, profile.SideInset, precision: 8);
    }

    [Theory]
    [InlineData((int)StageCardLayoutTier.Compact, 199.999, 1)]
    [InlineData((int)StageCardLayoutTier.Compact, 200, 2)]
    [InlineData((int)StageCardLayoutTier.Compact, 299.999, 2)]
    [InlineData((int)StageCardLayoutTier.Compact, 300, 3)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 223.999, 1)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 224, 2)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 447.999, 3)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 448, 4)]
    public void ColumnBreakpointsChangeOnlyWhenAnotherMinimumCardFits(
        int tierValue,
        double viewportWidthDip,
        int expectedColumns)
    {
        var tier = (StageCardLayoutTier)tierValue;
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip, tier);

        Assert.Equal(expectedColumns, profile.ColumnCount);
    }

    [Theory]
    [InlineData((int)StageCardLayoutTier.Compact, 100, 92, 4)]
    [InlineData((int)StageCardLayoutTier.Compact, 124, 104, 10)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 112, 104, 4)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 148, 124, 12)]
    public void SingleColumnCardStretchesThenCentersAtItsMaximum(
        int tierValue,
        double viewportWidthDip,
        double expectedCardWidth,
        double expectedSideInset)
    {
        var tier = (StageCardLayoutTier)tierValue;
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip, tier);

        Assert.Equal(1, profile.ColumnCount);
        Assert.Equal(expectedCardWidth, profile.CardWidth, precision: 8);
        Assert.Equal(expectedSideInset, profile.SideInset, precision: 8);
    }

    [Theory]
    [InlineData((int)StageCardLayoutTier.Compact, 92)]
    [InlineData((int)StageCardLayoutTier.Compact, 260)]
    [InlineData((int)StageCardLayoutTier.Compact, 520)]
    [InlineData((int)StageCardLayoutTier.Compact, 800)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 104)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 260)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 520)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 800)]
    public void ProfileNeverConsumesMoreThanTheViewport(
        int tierValue,
        double viewportWidthDip)
    {
        var tier = (StageCardLayoutTier)tierValue;
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip, tier);
        var occupiedWidth =
            (2.0 * profile.SideInset) +
            (profile.ColumnCount * profile.CardWidth) +
            ((profile.ColumnCount - 1) * profile.HorizontalGap);

        Assert.Equal(viewportWidthDip, occupiedWidth, precision: 8);
        Assert.InRange(
            profile.ColumnCount,
            1,
            StageCardLayoutCalculator.MaximumColumnCount);
        Assert.True(profile.CardWidth > 0);
        Assert.True(profile.SideInset >= 0);
    }

    [Theory]
    [InlineData((int)StageCardLayoutTier.Compact, 104)]
    [InlineData((int)StageCardLayoutTier.ManagementSafe, 124)]
    public void WideViewportCapsCardWidthAndCentersTheBoundedRow(
        int tierValue,
        double expectedMaximumCardWidth)
    {
        var tier = (StageCardLayoutTier)tierValue;
        var profile = StageCardLayoutCalculator.Calculate(1600, tier);

        Assert.Equal(
            StageCardLayoutCalculator.MaximumColumnCount,
            profile.ColumnCount);
        Assert.Equal(expectedMaximumCardWidth, profile.CardWidth, precision: 8);
        Assert.True(profile.SideInset > StageCardLayoutCalculator.MinimumSideInset);
    }

    [Fact]
    public void LargestFiniteWidthStillUsesTheNamedMaximumColumnCount()
    {
        var profile = StageCardLayoutCalculator.Calculate(double.MaxValue);

        Assert.Equal(
            StageCardLayoutCalculator.MaximumColumnCount,
            profile.ColumnCount);
        Assert.True(double.IsFinite(profile.CardWidth));
        Assert.True(double.IsFinite(profile.SideInset));
    }

    [Fact]
    public void DefaultTierIsManagementSafeUntilTheVisualGateChoosesACandidate()
    {
        var profile = StageCardLayoutCalculator.Calculate(520);

        Assert.Equal(StageCardLayoutCalculator.DefaultTier, profile.Tier);
        Assert.Equal(StageCardLayoutTier.ManagementSafe, profile.Tier);
    }

    [Fact]
    public void RepeatedCalculationReturnsTheSameImmutableProfileValue()
    {
        var first = StageCardLayoutCalculator.Calculate(520);
        var second = StageCardLayoutCalculator.Calculate(520);

        Assert.Equal(first, second);
        Assert.NotSame(first, second);
    }
}
