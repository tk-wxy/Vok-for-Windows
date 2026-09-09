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
    [InlineData(260, 3, 72, 16)]
    [InlineData(520, 6, 72, 29)]
    [InlineData(720, 9, 72, 12)]
    [InlineData(784, 10, 72, 5)]
    [InlineData(800, 10, 72, 13)]
    [InlineData(960, 10, 72, 93)]
    public void FrozenProfileCoversNarrowAndWideStageViewports(
        double viewportWidthDip,
        int expectedColumns,
        double expectedCardWidth,
        double expectedSideInset)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);

        Assert.Equal(expectedColumns, profile.ColumnCount);
        Assert.Equal(expectedCardWidth, profile.CardWidth, precision: 8);
        Assert.Equal(StageCardLayoutCalculator.HorizontalGap, profile.HorizontalGap);
        Assert.Equal(expectedSideInset, profile.SideInset, precision: 8);
    }

    [Theory]
    [InlineData(157.999, 1)]
    [InlineData(158, 2)]
    [InlineData(235.999, 2)]
    [InlineData(236, 3)]
    [InlineData(703.999, 8)]
    [InlineData(704, 9)]
    [InlineData(781.999, 9)]
    [InlineData(782, 10)]
    public void ColumnBreakpointsChangeOnlyWhenAnotherMinimumCardFits(
        double viewportWidthDip,
        int expectedColumns)
    {
        var profile = StageCardLayoutCalculator.Calculate(viewportWidthDip);

        Assert.Equal(expectedColumns, profile.ColumnCount);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(72, 64, 4)]
    [InlineData(80, 72, 4)]
    [InlineData(100, 72, 14)]
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
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(157.999)]
    [InlineData(158)]
    [InlineData(260)]
    [InlineData(520)]
    [InlineData(720)]
    [InlineData(800)]
    [InlineData(782)]
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
