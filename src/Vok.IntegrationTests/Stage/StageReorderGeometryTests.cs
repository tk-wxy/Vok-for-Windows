using System.Windows;
using Vok.Features.Stage;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageReorderGeometryTests
{
    [Theory]
    [InlineData(3.9, 3.9, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(-8, 1, true)]
    public void DragStartsOnlyAtTheSystemDistanceBoundary(
        double x,
        double y,
        bool expected)
    {
        Assert.Equal(
            expected,
            StageReorderGeometry.HasExceededDragThreshold(
                new Vector(x, y),
                horizontalThreshold: 4,
                verticalThreshold: 4));
    }

    [Theory]
    [InlineData(9, 12, 0)]
    [InlineData(39, 12, 1)]
    [InlineData(69, 12, 2)]
    [InlineData(9, 52, 3)]
    [InlineData(39, 52, 4)]
    [InlineData(69, 52, 5)]
    public void UsesActualTwoDimensionalCardCenters(
        double pointerX,
        double pointerY,
        int expectedIndex)
    {
        var cards = CreateCards(itemCount: 6, columnCount: 3);

        Assert.Equal(
            expectedIndex,
            StageReorderGeometry.GetTargetIndex(
                new Point(pointerX, pointerY),
                cards,
                itemCount: 6));
    }

    [Fact]
    public void TailRowBlankSpaceNeverBecomesANonexistentItem()
    {
        var cards = CreateCards(itemCount: 5, columnCount: 3);

        Assert.Equal(
            4,
            StageReorderGeometry.GetTargetIndex(
                new Point(500, 52),
                cards,
                itemCount: 5));
    }

    [Fact]
    public void PointerOutsideRealizedRowsClampsToNearestRealizedRow()
    {
        var cards = CreateCards(itemCount: 6, columnCount: 3);

        Assert.Equal(
            0,
            StageReorderGeometry.GetTargetIndex(
                new Point(-100, -100),
                cards,
                itemCount: 6));
        Assert.Equal(
            5,
            StageReorderGeometry.GetTargetIndex(
                new Point(500, 500),
                cards,
                itemCount: 6));
    }

    [Theory]
    [InlineData(-10, -720)]
    [InlineData(18, -360)]
    [InlineData(50, 0)]
    [InlineData(82, 360)]
    [InlineData(110, 720)]
    public void AutoScrollVelocityIsBoundedAndProportional(
        double pointerY,
        double expectedVelocity)
    {
        Assert.Equal(
            expectedVelocity,
            StageReorderGeometry.GetAutoScrollVelocity(
                pointerY,
                viewportHeight: 100,
                edgeZone: 36,
                maximumSpeed: 720),
            precision: 6);
    }

    [Fact]
    public void PreviewOffsetsShiftOnlyTheCardsBetweenSourceAndTarget()
    {
        var cards = CreateCards(itemCount: 6, columnCount: 3);

        var offsets = StageReorderGeometry.GetPreviewOffsets(
            cards,
            sourceIndex: 1,
            targetIndex: 4);

        Assert.Equal(
            [
                new StageReorderCardOffset(2, new Vector(-30, 0)),
                new StageReorderCardOffset(3, new Vector(60, -40)),
                new StageReorderCardOffset(4, new Vector(-30, 0)),
            ],
            offsets);
    }

    [Fact]
    public void ReversePreviewOffsetsShiftCardsTowardTheVacatedSlot()
    {
        var cards = CreateCards(itemCount: 6, columnCount: 3);

        var offsets = StageReorderGeometry.GetPreviewOffsets(
            cards,
            sourceIndex: 4,
            targetIndex: 1);

        Assert.Equal(
            [
                new StageReorderCardOffset(1, new Vector(30, 0)),
                new StageReorderCardOffset(2, new Vector(-60, 40)),
                new StageReorderCardOffset(3, new Vector(30, 0)),
            ],
            offsets);
    }

    [Fact]
    public void PreviewAtTheOriginalSlotHasNoOffsets()
    {
        Assert.Empty(StageReorderGeometry.GetPreviewOffsets(
            CreateCards(itemCount: 6, columnCount: 3),
            sourceIndex: 2,
            targetIndex: 2));
    }

    private static IReadOnlyList<StageReorderCardBounds> CreateCards(
        int itemCount,
        int columnCount) =>
        Enumerable.Range(0, itemCount)
            .Select(index => new StageReorderCardBounds(
                index,
                new Rect(
                    x: (index % columnCount) * 30,
                    y: (index / columnCount) * 40,
                    width: 20,
                    height: 24)))
            .ToArray();
}
