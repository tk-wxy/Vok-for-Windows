using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class StageOrderRebalancePlannerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public void DenseEndpointUsesTheSmallestCompleteBoundedWindow(int itemCount)
    {
        var items = CreateDenseItems(itemCount);
        var source = items[0];
        var olderCandidates = items.Skip(1).Reverse().ToArray();

        var plan = StageOrderRebalancePlanner.TryCreate(
            source,
            [],
            olderCandidates,
            SqliteStageRepository.SortOrderStep);

        var applied = Assert.IsType<StageOrderRebalancePlan>(plan);
        Assert.Equal(itemCount, applied.Assignments.Count);
        Assert.Equal(itemCount, applied.ChangedCount);
        Assert.Equal(source.Id, applied.Assignments[0].Item.Id);
        Assert.Equal(
            olderCandidates.Select(static item => item.Id),
            applied.Assignments.Skip(1).Select(static assignment => assignment.Item.Id));
        AssertStrictlyDescending(applied.Assignments);
    }

    [Fact]
    public void DenseEndpointRefusesAWindowThatWouldChangeSixtyFiveItems()
    {
        var items = CreateDenseItems(
            StageReorderResult.MaximumChangedItems + 1);
        var source = items[0];
        var olderCandidates = items.Skip(1).Reverse().ToArray();

        var plan = StageOrderRebalancePlanner.TryCreate(
            source,
            [],
            olderCandidates,
            SqliteStageRepository.SortOrderStep);

        Assert.Null(plan);
    }

    [Fact]
    public void PlannerUsesTheSmallestFeasibleWindowBetweenExternalBounds()
    {
        var source = Item(1, 0);
        var newer = Item(4, 100);
        var outerNewer = Item(5, 101);
        var older = Item(3, 99);
        var outerOlder = Item(2, 0);

        var plan = StageOrderRebalancePlanner.TryCreate(
            source,
            [newer, outerNewer],
            [older, outerOlder],
            SqliteStageRepository.SortOrderStep);

        var applied = Assert.IsType<StageOrderRebalancePlan>(plan);
        Assert.Equal(2, applied.Assignments.Count);
        Assert.Equal([source.Id, older.Id],
            applied.Assignments.Select(static assignment => assignment.Item.Id));
        Assert.True(applied.Assignments[0].SortOrder < newer.SortOrder);
        Assert.True(applied.Assignments[^1].SortOrder > outerOlder.SortOrder);
        AssertStrictlyDescending(applied.Assignments);
    }

    private static StageItem[] CreateDenseItems(int count) =>
        Enumerable.Range(1, count)
            .Select(index => Item(index, long.MaxValue))
            .ToArray();

    private static StageItem Item(int index, long sortOrder) => new(
        Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
        StageItemKind.File,
        $"C:\\Stage\\{index:D3}.txt",
        $"C:\\STAGE\\{index:D3}.TXT",
        $"{index:D3}.txt",
        sortOrder,
        false,
        1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static void AssertStrictlyDescending(
        IReadOnlyList<StageOrderRebalanceAssignment> assignments)
    {
        for (var index = 1; index < assignments.Count; index++)
        {
            Assert.True(
                assignments[index - 1].SortOrder > assignments[index].SortOrder);
        }
    }
}
