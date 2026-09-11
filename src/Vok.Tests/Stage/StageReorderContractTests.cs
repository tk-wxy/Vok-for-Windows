using Vok.Core.Stage;
using Xunit;

namespace Vok.Tests.Stage;

public sealed class StageReorderContractTests
{
    [Fact]
    public void ItemVersionRejectsEmptyIdentityAndNonPositiveRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageItemVersion(Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageItemVersion(Guid.NewGuid(), 0));
    }

    [Fact]
    public void OrderAnchorRejectsEmptyIdentityAndNonPositiveRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageOrderAnchor(Guid.Empty, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageOrderAnchor(Guid.NewGuid(), -1, 0));
    }

    [Fact]
    public void RequestRejectsSourceOrDuplicateAnchors()
    {
        var source = new StageItemVersion(Guid.NewGuid(), 1);
        var anchor = new StageOrderAnchor(Guid.NewGuid(), 1, 20);

        Assert.Throws<ArgumentException>(() => new StageReorderRequest(
            source,
            new StageOrderAnchor(source.Id, source.Revision, 30),
            anchor));
        Assert.Throws<ArgumentException>(() => new StageReorderRequest(
            source,
            anchor,
            anchor));
    }

    [Fact]
    public void RequestRejectsDefaultSourceAndAnchors()
    {
        var source = new StageItemVersion(Guid.NewGuid(), 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageReorderRequest(default, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageReorderRequest(source, default(StageOrderAnchor), null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StageReorderRequest(source, null, default(StageOrderAnchor)));
    }

    [Fact]
    public void RequestRejectsInvertedCompositeOrder()
    {
        var source = new StageItemVersion(Guid.NewGuid(), 1);
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        Assert.Throws<ArgumentException>(() => new StageReorderRequest(
            source,
            new StageOrderAnchor(Guid.NewGuid(), 1, 10),
            new StageOrderAnchor(Guid.NewGuid(), 1, 20)));
        Assert.Throws<ArgumentException>(() => new StageReorderRequest(
            source,
            new StageOrderAnchor(lowerId, 1, 10),
            new StageOrderAnchor(higherId, 1, 10)));
    }

    [Fact]
    public void AnchorComparisonUsesSortOrderThenCanonicalTextIdentity()
    {
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var lower = new StageOrderAnchor(lowerId, 1, 10);
        var higher = new StageOrderAnchor(higherId, 1, 10);

        Assert.True(higher.CompareTo(lower) > 0);
        Assert.True(new StageOrderAnchor(lowerId, 1, 11).CompareTo(higher) > 0);
    }

    [Fact]
    public void ResultRequiresChangedItemsOnlyForAppliedStatus()
    {
        var item = CreateItem();

        Assert.Throws<ArgumentException>(() =>
            new StageReorderResult(StageReorderStatus.Applied, []));
        Assert.Throws<ArgumentException>(() =>
            new StageReorderResult(StageReorderStatus.NoChange, [item]));

        var input = new List<StageItem> { item };
        var applied = new StageReorderResult(StageReorderStatus.Applied, input);
        input.Clear();
        var unchanged = new StageReorderResult(StageReorderStatus.NoChange, []);
        Assert.Equal(item, Assert.Single(applied.ChangedItems));
        Assert.Empty(unchanged.ChangedItems);
    }

    [Fact]
    public void ExistingStagePageConstructionKeepsOptionalBoundariesEmpty()
    {
        var page = new StagePage([], null, 0);

        Assert.Null(page.NewerBoundary);
        Assert.Null(page.OlderBoundary);
    }

    private static StageItem CreateItem() => new(
        Guid.NewGuid(),
        StageItemKind.File,
        @"C:\Stage\item.txt",
        @"C:\STAGE\ITEM.TXT",
        "item.txt",
        10,
        false,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
