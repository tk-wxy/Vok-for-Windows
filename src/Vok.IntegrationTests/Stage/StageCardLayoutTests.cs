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

    [Theory]
    [InlineData(260, 2, 122, 4)]
    [InlineData(520, 4, 122, 4)]
    [InlineData(720, 6, 112, 4)]
    [InlineData(800, 7, 106.28571428571429, 4)]
    [InlineData(960, 8, 112, 4)]
    public void FrozenProfileCoversNarrowAndWideStageViewports(
        double viewportWidthDip,
        int expectedColumns,
        double expectedCardWidth,
        double expectedSideInset)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);

        Assert.Equal(expectedColumns, profile.ColumnCount);
        Assert.Equal(expectedCardWidth, profile.CardWidth, precision: 8);
        Assert.Equal(StageCardLayoutCalculator.CardHeight, profile.CardHeight);
        Assert.Equal(
            StageCardLayoutCalculator.CardHeight + StageCardLayoutCalculator.VerticalGap,
            profile.RowUnitHeight,
            precision: 8);
        Assert.Equal(StageCardLayoutCalculator.HorizontalGap, profile.HorizontalGap);
        Assert.Equal(StageCardLayoutCalculator.VerticalGap, profile.VerticalGap);
        Assert.Equal(expectedSideInset, profile.SideInset, precision: 8);
    }

    [Theory]
    [InlineData(111.999, 1)]
    [InlineData(112, 1)]
    [InlineData(223.999, 1)]
    [InlineData(224, 2)]
    [InlineData(447.999, 3)]
    [InlineData(448, 4)]
    [InlineData(895.999, 7)]
    [InlineData(896, 8)]
    public void ColumnBreakpointsChangeOnlyWhenAnotherMinimumCardFits(
        double viewportWidthDip,
        int expectedColumns)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);

        Assert.Equal(expectedColumns, profile.ColumnCount);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(104, 96, 4)]
    [InlineData(112, 104, 4)]
    [InlineData(148, 124, 12)]
    public void SingleColumnFallbackStaysInsideTheViewportAndCentersWhenPossible(
        double viewportWidthDip,
        double expectedCardWidth,
        double expectedSideInset)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);

        Assert.Equal(1, profile.ColumnCount);
        Assert.Equal(expectedCardWidth, profile.CardWidth, precision: 8);
        Assert.Equal(expectedSideInset, profile.SideInset, precision: 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(104)]
    [InlineData(112)]
    [InlineData(223.999)]
    [InlineData(224)]
    [InlineData(260)]
    [InlineData(520)]
    [InlineData(720)]
    [InlineData(800)]
    [InlineData(896)]
    [InlineData(960)]
    [InlineData(1600)]
    public void ProfileNeverConsumesMoreThanTheViewport(double viewportWidthDip)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);
        var occupiedWidth =
            (2.0 * profile.SideInset) +
            (profile.ColumnCount * profile.CardWidth) +
            ((profile.ColumnCount - 1) * profile.HorizontalGap);

        Assert.Equal(viewportWidthDip, occupiedWidth, precision: 8);
        Assert.InRange(profile.ColumnCount, 1, StageCardLayoutCalculator.MaximumColumnCount);
        Assert.True(profile.CardWidth > 0);
        Assert.True(profile.SideInset >= 0);
    }

    [Fact]
    public void WideViewportCapsCardWidthAndCentersTheBoundedRow()
    {
        var profile = StageCardLayoutCalculator.Calculate(1600);

        Assert.Equal(StageCardLayoutCalculator.MaximumColumnCount, profile.ColumnCount);
        Assert.Equal(StageCardLayoutCalculator.MaximumCardWidth, profile.CardWidth);
        Assert.True(profile.SideInset > StageCardLayoutCalculator.MinimumSideInset);
    }

    [Fact]
    public void LargestFiniteWidthStillUsesTheNamedMaximumColumnCount()
    {
        var profile = StageCardLayoutCalculator.Calculate(double.MaxValue);

        Assert.Equal(StageCardLayoutCalculator.MaximumColumnCount, profile.ColumnCount);
        Assert.True(double.IsFinite(profile.CardWidth));
        Assert.True(double.IsFinite(profile.SideInset));
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
