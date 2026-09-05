using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class SqliteStageRepositoryTests
{
    [Fact]
    public async Task SetPinnedRequiresCurrentRevisionAndReturnsTheAuthoritativeItem()
    {
        var directory = CreateTestDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "pin.db");
            using var repository = new SqliteStageRepository(databasePath);
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\Pinned.txt", "Pinned")],
                TestContext.Current.CancellationToken);
            var original = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);

            var pinned = await repository.SetPinnedAsync(
                original.Id,
                original.Revision,
                isPinned: true,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageItemMutationStatus.Applied, pinned.Status);
            var authoritative = Assert.IsType<StageItem>(pinned.Item);
            Assert.True(authoritative.IsPinned);
            Assert.Equal(original.Id, authoritative.Id);
            Assert.Equal(original.Kind, authoritative.Kind);
            Assert.Equal(original.TargetValue, authoritative.TargetValue);
            Assert.Equal(original.CanonicalKey, authoritative.CanonicalKey);
            Assert.Equal(original.ResolvedName, authoritative.ResolvedName);
            Assert.Equal(original.SortOrder, authoritative.SortOrder);
            Assert.Equal(original.CreatedUtc, authoritative.CreatedUtc);
            Assert.Equal(original.Revision + 1, authoritative.Revision);
            Assert.True(authoritative.UpdatedUtc >= original.UpdatedUtc);

            var noChange = await repository.SetPinnedAsync(
                authoritative.Id,
                authoritative.Revision,
                isPinned: true,
                TestContext.Current.CancellationToken);
            Assert.Equal(StageItemMutationStatus.NoChange, noChange.Status);
            Assert.Equal(authoritative, noChange.Item);

            var conflict = await repository.SetPinnedAsync(
                original.Id,
                original.Revision,
                isPinned: false,
                TestContext.Current.CancellationToken);
            Assert.Equal(StageItemMutationStatus.RevisionConflict, conflict.Status);
            Assert.Null(conflict.Item);

            var missing = await repository.SetPinnedAsync(
                Guid.NewGuid(),
                expectedRevision: 1,
                isPinned: true,
                TestContext.Current.CancellationToken);
            Assert.Equal(StageItemMutationStatus.NotFound, missing.Status);
            Assert.Null(missing.Item);

            var unpinned = await repository.SetPinnedAsync(
                authoritative.Id,
                authoritative.Revision,
                isPinned: false,
                TestContext.Current.CancellationToken);
            Assert.Equal(StageItemMutationStatus.Applied, unpinned.Status);
            var authoritativeUnpinned = Assert.IsType<StageItem>(unpinned.Item);
            Assert.False(authoritativeUnpinned.IsPinned);
            Assert.Equal(authoritative.Revision + 1, authoritativeUnpinned.Revision);

            using var reopened = new SqliteStageRepository(databasePath);
            var persisted = Assert.Single((await reopened.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            Assert.Equal(authoritativeUnpinned, persisted);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SetPinnedRevisionOverflowRollsBackWithoutChangingTheItem()
    {
        var directory = CreateTestDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "pin-overflow.db");
            using var executor = new SqliteStorageExecutor(databasePath);
            using var repository = new SqliteStageRepository(
                executor,
                new StageOptions());
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\Overflow.txt", "Overflow")],
                TestContext.Current.CancellationToken);
            var original = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            await executor.ExecuteAsync(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE stage_items SET revision = $revision WHERE id = $id;";
                    command.Parameters.AddWithValue("$revision", long.MaxValue);
                    command.Parameters.AddWithValue("$id", original.Id.ToString("D"));
                    Assert.Equal(1, command.ExecuteNonQuery());
                    return true;
                },
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<OverflowException>(() => repository.SetPinnedAsync(
                original.Id,
                long.MaxValue,
                isPinned: true,
                TestContext.Current.CancellationToken));

            var after = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            Assert.False(after.IsPinned);
            Assert.Equal(long.MaxValue, after.Revision);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RemoveRequiresCurrentRevisionAndNeverTouchesTheSourceFile()
    {
        var directory = CreateTestDirectory();
        var source = Path.Combine(directory, "source.txt");
        var databasePath = Path.Combine(directory, "remove.db");
        try
        {
            await File.WriteAllTextAsync(
                source,
                "source-content",
                TestContext.Current.CancellationToken);
            var sourceLength = new FileInfo(source).Length;
            var sourceWriteTime = File.GetLastWriteTimeUtc(source);
            var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(
                source,
                TestContext.Current.CancellationToken));
            using var repository = new SqliteStageRepository(databasePath);
            _ = await repository.AddOrMoveAsync(
                [Target(source, "source.txt")],
                TestContext.Current.CancellationToken);
            var original = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);

            _ = await repository.AddOrMoveAsync(
                [Target(source, "source refreshed")],
                TestContext.Current.CancellationToken);
            var conflict = await repository.RemoveAsync(
                original.Id,
                original.Revision,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRemoveStatus.RevisionConflict, conflict.Status);
            var current = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            Assert.Equal(original.Id, current.Id);
            Assert.Equal(original.Revision + 1, current.Revision);

            var removed = await repository.RemoveAsync(
                current.Id,
                current.Revision,
                TestContext.Current.CancellationToken);
            var missing = await repository.RemoveAsync(
                current.Id,
                current.Revision,
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRemoveStatus.Removed, removed.Status);
            Assert.Equal(StageRemoveStatus.NotFound, missing.Status);
            Assert.Equal(0, (await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).TotalCount);
            Assert.True(File.Exists(source));
            Assert.Equal(sourceLength, new FileInfo(source).Length);
            Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(source));
            Assert.Equal(
                sourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(
                    source,
                    TestContext.Current.CancellationToken)));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task AddAndMixedDuplicateMovePreserveInputOrderAndUnaffectedRows()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "add-move.db");
        try
        {
            using var executor = new SqliteStorageExecutor(databasePath);
            using var repository = new SqliteStageRepository(
                executor,
                new StageOptions(capacity: 50));
            var first = await repository.AddOrMoveAsync(
                [Target("C:\\A.txt", "A"), Target("C:\\B.txt", "B"), Target("C:\\C.txt", "C")],
                TestContext.Current.CancellationToken);
            Assert.Equal(StageAddOrMoveStatus.Applied, first.Status);
            Assert.Equal(3, first.AddedCount);
            Assert.Equal(0, first.MovedCount);

            var initial = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(["A", "B", "C"], initial.Items.Select(static item => item.ResolvedName));
            var aBefore = initial.Items[0];
            var bBefore = initial.Items[1];
            var cBefore = initial.Items[2];
            var aPinned = Assert.IsType<StageItem>((await repository.SetPinnedAsync(
                aBefore.Id,
                aBefore.Revision,
                isPinned: true,
                TestContext.Current.CancellationToken)).Item);

            var moved = await repository.AddOrMoveAsync(
                [
                    Target("c:\\c.TXT", "C refreshed"),
                    Target("C:\\D.txt", "D"),
                    Target("c:\\a.txt", "A refreshed"),
                    Target("C:\\C.txt", "C ignored duplicate"),
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageAddOrMoveStatus.Applied, moved.Status);
            Assert.Equal(3, moved.CountBefore);
            Assert.Equal(4, moved.CountAfter);
            Assert.Equal(1, moved.AddedCount);
            Assert.Equal(2, moved.MovedCount);
            var page = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ["C refreshed", "D", "A refreshed", "B"],
                page.Items.Select(static item => item.ResolvedName));
            Assert.Equal(cBefore.Id, page.Items[0].Id);
            Assert.Equal(aBefore.Id, page.Items[2].Id);
            Assert.True(page.Items[2].IsPinned);
            Assert.Equal(aPinned.CreatedUtc, page.Items[2].CreatedUtc);
            Assert.Equal(2, page.Items[0].Revision);
            Assert.Equal(3, page.Items[2].Revision);
            Assert.Equal(bBefore.Id, page.Items[3].Id);
            Assert.Equal(bBefore.Revision, page.Items[3].Revision);
            Assert.Equal(bBefore.SortOrder, page.Items[3].SortOrder);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CapacityFailureRejectsTheWholeValidUniqueBatch()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteStageRepository(
                Path.Combine(directory, "capacity.db"),
                new StageOptions(capacity: 3));
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\A.txt", "A"), Target("C:\\B.txt", "B")],
                TestContext.Current.CancellationToken);
            var before = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);

            var rejected = await repository.AddOrMoveAsync(
                [
                    Target("c:\\a.txt", "A changed"),
                    Target("C:\\C.txt", "C"),
                    Target("C:\\D.txt", "D"),
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageAddOrMoveStatus.CapacityExceeded, rejected.Status);
            Assert.Equal(2, rejected.CountBefore);
            Assert.Equal(2, rejected.CountAfter);
            Assert.Equal(0, rejected.AddedCount);
            Assert.Equal(0, rejected.MovedCount);
            var after = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(before.Items, after.Items);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ExistingItemCanRefreshAtCapacityWithoutOpeningANewSlot()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteStageRepository(
                Path.Combine(directory, "at-capacity.db"),
                new StageOptions(capacity: 2));
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\A.txt", "A"), Target("C:\\B.txt", "B")],
                TestContext.Current.CancellationToken);

            var refreshed = await repository.AddOrMoveAsync(
                [Target("c:\\a.TXT", "A refreshed")],
                TestContext.Current.CancellationToken);
            var rejected = await repository.AddOrMoveAsync(
                [Target("C:\\C.txt", "C")],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageAddOrMoveStatus.Applied, refreshed.Status);
            Assert.Equal(0, refreshed.AddedCount);
            Assert.Equal(1, refreshed.MovedCount);
            Assert.Equal(StageAddOrMoveStatus.CapacityExceeded, rejected.Status);
            var page = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal(
                ["A refreshed", "B"],
                page.Items.Select(static item => item.ResolvedName));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RevisionOverflowRollsBackEarlierWritesInTheBatch()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "revision-overflow.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO stage_items VALUES
                    ('00000000-0000-0000-0000-000000000001', 0,
                     'C:\Existing', 'C:\EXISTING', 'Existing',
                     1, 0, {long.MaxValue}, '{UtcText}', '{UtcText}');
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(databasePath);
            await Assert.ThrowsAsync<OverflowException>(() =>
                repository.AddOrMoveAsync(
                    [
                        Target("C:\\InsertedFirst.txt", "Inserted first"),
                        Target("c:\\existing", "Existing changed"),
                    ],
                    TestContext.Current.CancellationToken));

            var page = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            var item = Assert.Single(page.Items);
            Assert.Equal("Existing", item.ResolvedName);
            Assert.Equal(long.MaxValue, item.Revision);
            Assert.Equal(1, item.SortOrder);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentAddsCannotExceedCapacity()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteStageRepository(
                Path.Combine(directory, "concurrent.db"),
                new StageOptions(capacity: 1));
            var results = await Task.WhenAll(
                repository.AddOrMoveAsync(
                    [Target("C:\\A.txt", "A")],
                    TestContext.Current.CancellationToken),
                repository.AddOrMoveAsync(
                    [Target("C:\\B.txt", "B")],
                    TestContext.Current.CancellationToken));

            Assert.Equal(1, results.Count(static result =>
                result.Status is StageAddOrMoveStatus.Applied));
            Assert.Equal(1, results.Count(static result =>
                result.Status is StageAddOrMoveStatus.CapacityExceeded));
            Assert.Equal(
                1,
                (await repository.LoadPageAsync(
                    null,
                    10,
                    TestContext.Current.CancellationToken)).TotalCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CompositeCursorDoesNotSkipEqualSortOrders()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "cursor.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO stage_items VALUES
                    ('{lowerId:D}', 0, 'C:\\A', 'C:\\A', 'A', 10, 0, 1, '{UtcText}', '{UtcText}'),
                    ('{higherId:D}', 0, 'C:\\B', 'C:\\B', 'B', 10, 0, 1, '{UtcText}', '{UtcText}');
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(databasePath);
            var first = await repository.LoadPageAsync(
                null,
                1,
                TestContext.Current.CancellationToken);
            var second = await repository.LoadPageAsync(
                first.NextCursor,
                1,
                TestContext.Current.CancellationToken);

            Assert.Equal(higherId, Assert.Single(first.Items).Id);
            Assert.Equal(lowerId, Assert.Single(second.Items).Id);
            Assert.Null(second.NextCursor);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task TenThousandRowsPageWithoutGapsAndUseTheOrderIndex()
    {
        const int itemCount = 10_000;
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "scale.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO stage_items (
                        id, target_kind, target_value, canonical_key,
                        resolved_name, sort_order, is_pinned, revision,
                        created_utc, updated_utc)
                    VALUES (
                        $id, 0, $target, $key,
                        $name, $sortOrder, 0, 1,
                        $utc, $utc);
                    """;
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var target = command.Parameters.Add("$target", SqliteType.Text);
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var name = command.Parameters.Add("$name", SqliteType.Text);
                var sortOrder = command.Parameters.Add("$sortOrder", SqliteType.Integer);
                command.Parameters.AddWithValue("$utc", UtcText);
                for (var index = 1; index <= itemCount; index++)
                {
                    var path = $"C:\\Scale\\{index:D5}.txt";
                    id.Value = Guid.NewGuid().ToString("D");
                    target.Value = path;
                    key.Value = path.ToUpperInvariant();
                    name.Value = index.ToString("D5", CultureInfo.InvariantCulture);
                    sortOrder.Value = index * SqliteStageRepository.SortOrderStep;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            using (var repository = new SqliteStageRepository(
                databasePath,
                new StageOptions(capacity: itemCount)))
            {
                var ids = new HashSet<Guid>();
                StagePageCursor? cursor = null;
                long totalCount = 0;
                do
                {
                    var page = await repository.LoadPageAsync(
                        cursor,
                        127,
                        TestContext.Current.CancellationToken);
                    totalCount = page.TotalCount;
                    Assert.All(page.Items, item => Assert.True(ids.Add(item.Id)));
                    cursor = page.NextCursor;
                }
                while (cursor is not null);

                Assert.Equal(itemCount, totalCount);
                Assert.Equal(itemCount, ids.Count);
            }

            using var planConnection = OpenDatabase(databasePath);
            using var planCommand = planConnection.CreateCommand();
            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM stage_items
                WHERE sort_order < 9000000000
                   OR (sort_order = 9000000000 AND id < 'ffffffff-ffff-ffff-ffff-ffffffffffff')
                ORDER BY sort_order DESC, id DESC
                LIMIT 127;
                """;
            using var reader = planCommand.ExecuteReader();
            var details = new List<string>();
            while (reader.Read())
            {
                details.Add(reader.GetString(3));
            }

            Assert.Contains(details, detail =>
                detail.Contains("ix_stage_items_order", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SortOrderOverflowReturnsAConservativeNoOp()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "overflow.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO stage_items VALUES
                    ('00000000-0000-0000-0000-000000000001', 0,
                     'C:\\Existing', 'C:\\EXISTING', 'Existing',
                     {long.MaxValue - 1}, 0, 1, '{UtcText}', '{UtcText}');
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(
                databasePath,
                new StageOptions(capacity: 50));
            var result = await repository.AddOrMoveAsync(
                [Target("C:\\New.txt", "New")],
                TestContext.Current.CancellationToken);

            Assert.Equal(StageAddOrMoveStatus.SortOrderExhausted, result.Status);
            var page = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal("Existing", Assert.Single(page.Items).ResolvedName);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidArgumentsFailBeforeStorageWorkIsQueued()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteStageRepository(
                Path.Combine(directory, "validation.db"));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.AddOrMoveAsync([], TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.AddOrMoveAsync(
                    [Target("C:\\A", "")],
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.LoadPageAsync(
                    new StagePageCursor(0, Guid.Empty),
                    10,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.LoadPageAsync(
                    null,
                    257,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.RemoveAsync(
                    Guid.Empty,
                    1,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.RemoveAsync(
                    Guid.NewGuid(),
                    0,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.SetPinnedAsync(
                    Guid.Empty,
                    1,
                    isPinned: true,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.SetPinnedAsync(
                    Guid.NewGuid(),
                    0,
                    isPinned: true,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static StageTargetDescriptor Target(
        string path,
        string name,
        StageItemKind kind = StageItemKind.File) =>
        new(kind, path, path.ToUpperInvariant(), name);

    private static async Task InitializeStageSchemaAsync(string databasePath)
    {
        using var executor = new SqliteStorageExecutor(databasePath);
        _ = await executor.ExecuteAsync(
            static connection => connection.State,
            TestContext.Current.CancellationToken);
    }

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string UtcText = "2026-09-04T00:00:00.0000000+00:00";
}
