using System.Collections.ObjectModel;
using System.Windows.Threading;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageCardRowsProjectionTests(WpfApplicationFixture wpf)
{
    [Theory]
    [InlineData(0, 4, 0, 0)]
    [InlineData(1, 4, 1, 1)]
    [InlineData(8, 4, 2, 4)]
    [InlineData(10, 4, 3, 2)]
    [InlineData(64, 7, 10, 1)]
    [InlineData(64, 8, 8, 8)]
    public void ProjectionKeepsTheFlatOrderAndCreatesOnlyRealTailItems(
        int itemCount,
        int columnCount,
        int expectedRows,
        int expectedTailCount)
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(itemCount);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            projection.SetColumnCount(columnCount);
            projection.Attach(source);

            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(expectedRows, projection.Rows.Count);
            Assert.Equal(source, projection.Rows.SelectMany(static row => row.Items));
            if (expectedRows > 0)
            {
                Assert.Equal(expectedTailCount, projection.Rows[^1].Items.Count);
                Assert.All(projection.Rows, row =>
                {
                    Assert.Equal(columnCount, row.ColumnCount);
                    Assert.InRange(row.Items.Count, 1, columnCount);
                });
            }
        });
    }

    [Fact]
    public void ClearThenManyAddsPublishOnlyTheFinalPageOnce()
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(8);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            projection.SetColumnCount(4);
            projection.Attach(source);
            var published = 0;
            projection.RowsPublished += () => published++;
            PumpProjection(wpf.Application.Dispatcher);
            Assert.Equal(1, published);

            var replacement = CreateItems(64);
            source.Clear();
            foreach (var item in replacement)
            {
                source.Add(item);
            }

            Assert.Empty(projection.Rows);
            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(2, published);
            Assert.Equal(16, projection.Rows.Count);
            Assert.Equal(replacement, projection.Rows.SelectMany(static row => row.Items));
        });
    }

    [Fact]
    public void AddRemoveAndMoveEachPublishTheCurrentAuthoritativeOrder()
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(7);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            projection.SetColumnCount(3);
            projection.Attach(source);
            PumpProjection(wpf.Application.Dispatcher);

            var added = CreateItem(99);
            source.Add(added);
            source.Move(7, 1);
            source.RemoveAt(4);
            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(source, projection.Rows.SelectMany(static row => row.Items));
            Assert.Equal(3, projection.Rows.Count);
            _ = Assert.Single(projection.Rows[^1].Items);
        });
    }

    [Fact]
    public void EdgeLikeShiftKeepsExistingRowsAndTheirObservableItemSlots()
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(192);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            projection.SetColumnCount(10);
            projection.Attach(source);
            PumpProjection(wpf.Application.Dispatcher);
            var rows = projection.Rows.ToArray();
            var itemSlots = rows.Select(static row => row.Items).ToArray();

            for (var index = 0; index < 64; index++)
            {
                source.RemoveAt(0);
            }

            foreach (var item in CreateItems(64, start: 1000))
            {
                source.Add(item);
            }

            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(rows, projection.Rows);
            Assert.Equal(itemSlots, projection.Rows.Select(static row => row.Items));
            Assert.Equal(source, projection.Rows.SelectMany(static row => row.Items));
        });
    }

    [Fact]
    public void ColumnChangesReplaceAPendingLayoutAndPublishOnlyTheLatestShape()
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(11);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            var published = 0;
            projection.RowsPublished += () => published++;

            projection.Attach(source);
            projection.SetColumnCount(4);
            projection.SetColumnCount(6);
            projection.SetColumnCount(6);
            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(1, published);
            Assert.Equal(2, projection.Rows.Count);
            Assert.Equal(6, projection.Rows[0].Items.Count);
            Assert.Equal(5, projection.Rows[1].Items.Count);
            Assert.All(projection.Rows, row => Assert.Equal(6, row.ColumnCount));
        });
    }

    [Fact]
    public void ReattachingRejectsQueuedAndFutureChangesFromTheOldCollection()
    {
        wpf.Invoke(() =>
        {
            var oldSource = CreateItems(8);
            var currentSource = CreateItems(5, start: 100);
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            projection.SetColumnCount(4);
            projection.Attach(oldSource);
            projection.Attach(currentSource);
            oldSource.Add(CreateItem(999));
            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(currentSource, projection.Rows.SelectMany(static row => row.Items));
            Assert.DoesNotContain(
                projection.Rows.SelectMany(static row => row.Items),
                item => item.DisplayName == "Item 999");

            oldSource.Clear();
            PumpProjection(wpf.Application.Dispatcher);
            Assert.Equal(currentSource, projection.Rows.SelectMany(static row => row.Items));
        });
    }

    [Fact]
    public void DisposeCancelsQueuedPublicationAndReleasesTheSource()
    {
        wpf.Invoke(() =>
        {
            var source = CreateItems(8);
            var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            var published = 0;
            projection.RowsPublished += () => published++;
            projection.Attach(source);
            projection.Dispose();
            source.Add(CreateItem(999));
            PumpProjection(wpf.Application.Dispatcher);

            Assert.Equal(0, published);
            Assert.Empty(projection.Rows);
            Assert.Throws<ObjectDisposedException>(() => projection.Attach(source));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void InvalidColumnCountsAreRejected(int columnCount)
    {
        wpf.Invoke(() =>
        {
            using var projection = new StageCardRowsProjection(wpf.Application.Dispatcher);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                projection.SetColumnCount(columnCount));
        });
    }

    private static ObservableCollection<StageItemViewModel> CreateItems(
        int count,
        int start = 0) =>
        new(Enumerable.Range(start, count).Select(CreateItem));

    private static StageItemViewModel CreateItem(int index)
    {
        var now = DateTimeOffset.UtcNow;
        return new StageItemViewModel(new StageItem(
            Guid.NewGuid(),
            StageItemKind.File,
            $@"C:\Stage\Item-{index}.txt",
            $@"C:\STAGE\ITEM-{index}.TXT",
            $"Item {index}",
            index + 1,
            false,
            1,
            now,
            now));
    }

    private static void PumpProjection(Dispatcher dispatcher) =>
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
}
