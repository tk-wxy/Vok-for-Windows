using Vok.Core.Platform;
using Vok.Features.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Launcher;

public sealed class LauncherLayoutTests
{
    [Theory]
    [InlineData(620, 490, 5, 98)]
    [InlineData(900, 770, 8, 96.25)]
    [InlineData(1040, 910, 9, 101.11111111111111)]
    public void CardUnitsFillTheViewportWithoutRemainder(
        double workAreaHeight,
        double expectedViewportHeight,
        int expectedRows,
        double expectedCardUnitHeight)
    {
        var profile = LauncherLayoutCalculator.CalculateForWorkArea(workAreaHeight);

        Assert.Equal(expectedViewportHeight, profile.ViewportHeight, precision: 8);
        Assert.Equal(expectedRows, profile.VisibleRowCount);
        Assert.Equal(expectedCardUnitHeight, profile.CardUnitHeight, precision: 8);
        Assert.Equal(
            profile.ViewportHeight,
            profile.VisibleRowCount * profile.CardUnitHeight,
            precision: 8);
        Assert.Equal(
            profile.CardUnitHeight - LauncherLayoutCalculator.CardVerticalInset,
            profile.CardSurfaceHeight,
            precision: 8);
    }

    [Fact]
    public void VeryShortViewportPreservesContentHeightBeforeMaximumPreference()
    {
        var profile = LauncherLayoutCalculator.CalculateForViewport(170);

        Assert.Equal(1, profile.VisibleRowCount);
        Assert.Equal(170, profile.CardUnitHeight);
        Assert.Equal(170, profile.ViewportHeight, precision: 8);
    }

    [Fact]
    public void PrewarmedAndRepeatedViewportResolutionsDoNotRecalculate()
    {
        var cache = new LauncherLayoutProfileCache();
        cache.Prewarm(
        [
            new DisplayWorkArea(1920, 900, 96, 96),
            new DisplayWorkArea(2560, 1440, 144, 144),
        ]);
        Assert.Equal(2, cache.CalculationCount);

        var first = cache.GetOrCreateForViewport(770);
        for (var index = 0; index < 100; index++)
        {
            Assert.Same(first, cache.GetOrCreateForViewport(770));
        }

        Assert.Equal(2, cache.CalculationCount);

        var changedViewport = cache.GetOrCreateForViewport(770.4);
        Assert.NotSame(first, changedViewport);
        Assert.Equal(3, cache.CalculationCount);
        Assert.Same(changedViewport, cache.GetOrCreateForViewport(770.4));
        Assert.Equal(3, cache.CalculationCount);
    }

    [Fact]
    public void DisplayWorkAreaConvertsPhysicalPixelsToDips()
    {
        var workArea = new DisplayWorkArea(2560, 1440, 144, 144);

        Assert.Equal(1706.6666666666667, workArea.WidthDip, precision: 8);
        Assert.Equal(960, workArea.HeightDip, precision: 8);
    }
}
