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
    public async Task RelinkRequiresMatchingKindAndRevisionAndPreservesItemIdentity()
    {
        var directory = CreateTestDirectory();
        var oldSource = Path.Combine(directory, "old.txt");
        var newSource = Path.Combine(directory, "new.txt");
        try
        {
            await File.WriteAllTextAsync(
                oldSource,
                "old-source",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                newSource,
                "new-source",
                TestContext.Current.CancellationToken);
            var oldSnapshot = await FileSnapshot.CreateAsync(oldSource);
            var newSnapshot = await FileSnapshot.CreateAsync(newSource);
            var databasePath = Path.Combine(directory, "relink.db");
            using var repository = new SqliteStageRepository(databasePath);
            _ = await repository.AddOrMoveAsync(
                [Target(oldSource, "old.txt"), Target("C:\\Neighbor.txt", "Neighbor")],
                TestContext.Current.CancellationToken);
            var original = (await repository.LoadPageAsync(
                    null,
                    10,
                    TestContext.Current.CancellationToken))
                .Items
                .Single(item => item.TargetValue == oldSource);
            var pinned = Assert.IsType<StageItem>((await repository.SetPinnedAsync(
                original.Id,
                original.Revision,
                isPinned: true,
                TestContext.Current.CancellationToken)).Item);

            var applied = await repository.RelinkAsync(
                pinned.Id,
                pinned.Revision,
                Target(newSource, "new.txt"),
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRelinkStatus.Applied, applied.Status);
            var authoritative = Assert.IsType<StageItem>(applied.Item);
            Assert.Equal(pinned.Id, authoritative.Id);
            Assert.Equal(StageItemKind.File, authoritative.Kind);
            Assert.Equal(newSource, authoritative.TargetValue);
            Assert.Equal(newSource.ToUpperInvariant(), authoritative.CanonicalKey);
            Assert.Equal("new.txt", authoritative.ResolvedName);
            Assert.Equal(pinned.SortOrder, authoritative.SortOrder);
            Assert.True(authoritative.IsPinned);
            Assert.Equal(pinned.CreatedUtc, authoritative.CreatedUtc);
            Assert.Equal(pinned.Revision + 1, authoritative.Revision);
            Assert.True(authoritative.UpdatedUtc >= pinned.UpdatedUtc);

            var noChange = await repository.RelinkAsync(
                authoritative.Id,
                authoritative.Revision,
                Target(newSource.ToUpperInvariant(), "ignored casing update"),
                TestContext.Current.CancellationToken);
            Assert.Equal(StageRelinkStatus.NoChange, noChange.Status);
            Assert.Equal(authoritative, noChange.Item);

            var stale = await repository.RelinkAsync(
                authoritative.Id,
                pinned.Revision,
                Target(oldSource, "old.txt"),
                TestContext.Current.CancellationToken);
            Assert.Equal(StageRelinkStatus.RevisionConflict, stale.Status);
            Assert.Null(stale.Item);

            var wrongKind = await repository.RelinkAsync(
                authoritative.Id,
                authoritative.Revision,
                Target(directory, "folder", StageItemKind.Directory),
                TestContext.Current.CancellationToken);
            Assert.Equal(StageRelinkStatus.KindMismatch, wrongKind.Status);
            Assert.Null(wrongKind.Item);

            var missing = await repository.RelinkAsync(
                Guid.NewGuid(),
                expectedRevision: 1,
                Target(newSource, "new.txt"),
                TestContext.Current.CancellationToken);
            Assert.Equal(StageRelinkStatus.NotFound, missing.Status);
            Assert.Null(missing.Item);

            using var reopened = new SqliteStageRepository(databasePath);
            var persisted = (await reopened.LoadPageAsync(
                    null,
                    10,
                    TestContext.Current.CancellationToken))
                .Items
                .Single(item => item.Id == original.Id);
            Assert.Equal(authoritative, persisted);
            await oldSnapshot.AssertUnchangedAsync();
            await newSnapshot.AssertUnchangedAsync();
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RelinkCanonicalConflictRejectsWithoutChangingEitherItem()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteStageRepository(
                Path.Combine(directory, "relink-conflict.db"));
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\A.txt", "A"), Target("C:\\B.txt", "B")],
                TestContext.Current.CancellationToken);
            var before = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            var source = before.Items.Single(item => item.ResolvedName == "A");

            var conflict = await repository.RelinkAsync(
                source.Id,
                source.Revision,
                Target("c:\\b.TXT", "B replacement"),
                TestContext.Current.CancellationToken);

            Assert.Equal(StageRelinkStatus.CanonicalConflict, conflict.Status);
            Assert.Null(conflict.Item);
            var after = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(before.TotalCount, after.TotalCount);
            Assert.Equal(before.NextCursor, after.NextCursor);
            Assert.Equal(before.Items, after.Items);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RelinkRevisionOverflowRollsBackWithoutChangingTheItem()
    {
        var directory = CreateTestDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "relink-overflow.db");
            using var executor = new SqliteStorageExecutor(databasePath);
            using var repository = new SqliteStageRepository(
                executor,
                new StageOptions());
            _ = await repository.AddOrMoveAsync(
                [Target("C:\\Original.txt", "Original")],
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

            await Assert.ThrowsAsync<OverflowException>(() => repository.RelinkAsync(
                original.Id,
                long.MaxValue,
                Target("C:\\Replacement.txt", "Replacement"),
                TestContext.Current.CancellationToken));

            var after = Assert.Single((await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            Assert.Equal(original.TargetValue, after.TargetValue);
            Assert.Equal(original.ResolvedName, after.ResolvedName);
            Assert.Equal(long.MaxValue, after.Revision);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

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
    public async Task ReorderMovesOnlyTheSourceToTopMiddleAndBottom()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-fast-path.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 4)
                .Select(index => Guid.Parse(
                    $"00000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            InsertStageItems(
                databasePath,
                new StageSeed(ids[0], StageItemKind.File, "C:\\A", "A", 40),
                new StageSeed(ids[1], StageItemKind.Directory, "C:\\B", "B", 30),
                new StageSeed(ids[2], StageItemKind.ShellLink, "C:\\C.lnk", "C", 20),
                new StageSeed(ids[3], StageItemKind.File, "C:\\D", "D", 10));

            using var repository = new SqliteStageRepository(databasePath);
            var original = (await LoadAllAsync(repository)).ToDictionary(
                static item => item.Id);

            var movedToTop = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(original[ids[3]]),
                    null,
                    Anchor(original[ids[0]])),
                TestContext.Current.CancellationToken);
            var afterTop = await LoadAllAsync(repository);
            var topSource = afterTop.Single(item => item.Id == ids[3]);
            Assert.Equal(StageReorderStatus.Applied, movedToTop.Status);
            Assert.Equal(topSource, Assert.Single(movedToTop.ChangedItems));
            Assert.Equal(
                [ids[3], ids[0], ids[1], ids[2]],
                afterTop.Select(static item => item.Id));
            Assert.Equal(40 + SqliteStageRepository.SortOrderStep, topSource.SortOrder);
            Assert.Equal(2, topSource.Revision);

            var movedToMiddle = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(topSource),
                    Anchor(afterTop.Single(item => item.Id == ids[1])),
                    Anchor(afterTop.Single(item => item.Id == ids[2]))),
                TestContext.Current.CancellationToken);
            var afterMiddle = await LoadAllAsync(repository);
            var middleSource = afterMiddle.Single(item => item.Id == ids[3]);
            Assert.Equal(StageReorderStatus.Applied, movedToMiddle.Status);
            Assert.Equal(middleSource, Assert.Single(movedToMiddle.ChangedItems));
            Assert.Equal(
                [ids[0], ids[1], ids[3], ids[2]],
                afterMiddle.Select(static item => item.Id));
            Assert.Equal(25, middleSource.SortOrder);
            Assert.Equal(3, middleSource.Revision);

            var movedToBottom = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(middleSource),
                    Anchor(afterMiddle.Single(item => item.Id == ids[2])),
                    null),
                TestContext.Current.CancellationToken);
            var afterBottom = await LoadAllAsync(repository);
            var bottomSource = afterBottom.Single(item => item.Id == ids[3]);
            Assert.Equal(StageReorderStatus.Applied, movedToBottom.Status);
            Assert.Equal(bottomSource, Assert.Single(movedToBottom.ChangedItems));
            Assert.Equal(
                [ids[0], ids[1], ids[2], ids[3]],
                afterBottom.Select(static item => item.Id));
            Assert.Equal(20 - SqliteStageRepository.SortOrderStep, bottomSource.SortOrder);
            Assert.Equal(4, bottomSource.Revision);

            AssertBusinessFieldsEqual(original[ids[3]], bottomSource);
            Assert.Equal(
                original.Where(pair => pair.Key != ids[3])
                    .OrderByDescending(pair => pair.Value.SortOrder)
                    .Select(static pair => pair.Value),
                afterBottom.Where(item => item.Id != ids[3]));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReorderConflictsAndNoChangeNeverWrite()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-conflicts.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 4)
                .Select(index => Guid.Parse(
                    $"10000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            InsertStageItems(
                databasePath,
                new StageSeed(ids[0], StageItemKind.File, "C:\\A", "A", 40),
                new StageSeed(ids[1], StageItemKind.File, "C:\\B", "B", 30),
                new StageSeed(ids[2], StageItemKind.File, "C:\\C", "C", 20),
                new StageSeed(ids[3], StageItemKind.File, "C:\\D", "D", 10));

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var items = before.ToDictionary(static item => item.Id);

            var missingSource = await repository.ReorderAsync(
                new StageReorderRequest(
                    new StageItemVersion(Guid.NewGuid(), 1),
                    null,
                    null),
                TestContext.Current.CancellationToken);
            var staleSource = await repository.ReorderAsync(
                new StageReorderRequest(
                    new StageItemVersion(ids[3], 2),
                    null,
                    Anchor(items[ids[0]])),
                TestContext.Current.CancellationToken);
            var missingAnchor = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[ids[3]]),
                    null,
                    new StageOrderAnchor(Guid.NewGuid(), 1, 40)),
                TestContext.Current.CancellationToken);
            var staleAnchor = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[ids[3]]),
                    null,
                    new StageOrderAnchor(ids[0], 2, 40)),
                TestContext.Current.CancellationToken);
            var changedAnchorOrder = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[ids[3]]),
                    null,
                    new StageOrderAnchor(ids[0], 1, 41)),
                TestContext.Current.CancellationToken);
            var interruptedSlot = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[ids[3]]),
                    Anchor(items[ids[0]]),
                    Anchor(items[ids[2]])),
                TestContext.Current.CancellationToken);
            var noChange = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[ids[1]]),
                    Anchor(items[ids[0]]),
                    Anchor(items[ids[2]])),
                TestContext.Current.CancellationToken);

            Assert.Equal(StageReorderStatus.NotFound, missingSource.Status);
            Assert.Equal(StageReorderStatus.RevisionConflict, staleSource.Status);
            Assert.Equal(StageReorderStatus.NeighborConflict, missingAnchor.Status);
            Assert.Equal(StageReorderStatus.RevisionConflict, staleAnchor.Status);
            Assert.Equal(StageReorderStatus.NeighborConflict, changedAnchorOrder.Status);
            Assert.Equal(StageReorderStatus.NeighborConflict, interruptedSlot.Status);
            Assert.Equal(StageReorderStatus.NoChange, noChange.Status);
            Assert.Empty(missingSource.ChangedItems);
            Assert.Empty(staleSource.ChangedItems);
            Assert.Empty(missingAnchor.ChangedItems);
            Assert.Empty(staleAnchor.ChangedItems);
            Assert.Empty(changedAnchorOrder.ChangedItems);
            Assert.Empty(interruptedSlot.ChangedItems);
            Assert.Empty(noChange.ChangedItems);
            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(63)]
    [InlineData(64)]
    public async Task ReorderRebalancesDenseEndpointWithinSixtyFourItems(
        int itemCount)
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, $"reorder-dense-{itemCount}.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var seeds = CreateDenseStageSeeds(itemCount);
            InsertStageItems(databasePath, seeds);

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var source = before.Single(item => item.Id == seeds[0].Id);
            var firstOther = before.First(item => item.Id != source.Id);
            var result = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(source),
                    null,
                    Anchor(firstOther)),
                TestContext.Current.CancellationToken);

            var after = await LoadAllAsync(repository);
            Assert.Equal(StageReorderStatus.Applied, result.Status);
            Assert.Equal(itemCount, result.ChangedItems.Count);
            Assert.Equal(after, result.ChangedItems);
            Assert.Equal(source.Id, after[0].Id);
            Assert.Equal(
                before.Where(item => item.Id != source.Id).Select(static item => item.Id),
                after.Skip(1).Select(static item => item.Id));
            Assert.All(after, static item => Assert.Equal(2, item.Revision));
            AssertStrictlyDescendingSortOrders(after);

            var originals = before.ToDictionary(static item => item.Id);
            Assert.All(after, item => AssertBusinessFieldsEqual(originals[item.Id], item));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReorderRefusesDenseEndpointThatWouldChangeSixtyFiveItems()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-dense-65.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var seeds = CreateDenseStageSeeds(
                StageReorderResult.MaximumChangedItems + 1);
            InsertStageItems(databasePath, seeds);

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var source = before.Single(item => item.Id == seeds[0].Id);
            var firstOther = before.First(item => item.Id != source.Id);
            var result = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(source),
                    null,
                    Anchor(firstOther)),
                TestContext.Current.CancellationToken);

            Assert.Equal(StageReorderStatus.SortOrderExhausted, result.Status);
            Assert.Empty(result.ChangedItems);
            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RebalanceReturnsOnlyChangedRowsAndPreservesOutsideBoundaries()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-local-window.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 5)
                .Select(index => Guid.Parse(
                    $"22000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            InsertStageItems(
                databasePath,
                new StageSeed(ids[0], StageItemKind.File, "C:\\Source", "Source", 0),
                new StageSeed(ids[1], StageItemKind.File, "C:\\Bottom", "Bottom", 0),
                new StageSeed(ids[2], StageItemKind.File, "C:\\Older", "Older", 99),
                new StageSeed(ids[3], StageItemKind.File, "C:\\Newer", "Newer", 100),
                new StageSeed(ids[4], StageItemKind.File, "C:\\Top", "Top", 200));

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var originals = before.ToDictionary(static item => item.Id);
            var result = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(originals[ids[0]]),
                    Anchor(originals[ids[3]]),
                    Anchor(originals[ids[2]])),
                TestContext.Current.CancellationToken);

            var after = await LoadAllAsync(repository);
            Assert.Equal(StageReorderStatus.Applied, result.Status);
            Assert.Equal(
                [ids[0], ids[2]],
                result.ChangedItems.Select(static item => item.Id));
            Assert.Equal(
                [ids[4], ids[3], ids[0], ids[2], ids[1]],
                after.Select(static item => item.Id));
            AssertStrictlyDescendingSortOrders(after);
            Assert.Equal(originals[ids[4]], after.Single(item => item.Id == ids[4]));
            Assert.Equal(originals[ids[3]], after.Single(item => item.Id == ids[3]));
            Assert.Equal(originals[ids[1]], after.Single(item => item.Id == ids[1]));
            Assert.All(result.ChangedItems, static item => Assert.Equal(2, item.Revision));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReorderRebalancesMiddleAndBottomNumericLimits()
    {
        var sourceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var newerId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var olderId = Guid.Parse("20000000-0000-0000-0000-000000000003");

        await AssertReorderAppliedAsync(
            [
                new StageSeed(sourceId, StageItemKind.File, "C:\\Source", "Source", 0),
                new StageSeed(newerId, StageItemKind.File, "C:\\Newer", "Newer", 11),
                new StageSeed(olderId, StageItemKind.File, "C:\\Older", "Older", 10),
            ],
            sourceId,
            newerId,
            olderId,
            [newerId, sourceId, olderId]);
        await AssertReorderAppliedAsync(
            [
                new StageSeed(sourceId, StageItemKind.File, "C:\\Source", "Source", 0),
                new StageSeed(newerId, StageItemKind.File, "C:\\Newer", "Newer", long.MinValue),
            ],
            sourceId,
            newerId,
            null,
            [newerId, sourceId]);
    }

    [Fact]
    public async Task ReorderRevisionOverflowRollsBackWithoutChangingAnyRow()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-revision-overflow.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var sourceId = Guid.Parse("30000000-0000-0000-0000-000000000001");
            var anchorId = Guid.Parse("30000000-0000-0000-0000-000000000002");
            InsertStageItems(
                databasePath,
                new StageSeed(
                    sourceId,
                    StageItemKind.File,
                    "C:\\Source",
                    "Source",
                    0,
                    long.MaxValue),
                new StageSeed(anchorId, StageItemKind.File, "C:\\Anchor", "Anchor", 10));

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var items = before.ToDictionary(static item => item.Id);
            await Assert.ThrowsAsync<OverflowException>(() =>
                repository.ReorderAsync(
                    new StageReorderRequest(
                        Version(items[sourceId]),
                        null,
                        Anchor(items[anchorId])),
                    TestContext.Current.CancellationToken));

            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RebalanceMemberRevisionOverflowRollsBackWithoutChangingAnyRow()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "rebalance-revision-overflow.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 3)
                .Select(index => Guid.Parse(
                    $"31000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            InsertStageItems(
                databasePath,
                new StageSeed(ids[0], StageItemKind.File, "C:\\Source", "Source", long.MaxValue),
                new StageSeed(
                    ids[1],
                    StageItemKind.File,
                    "C:\\Overflow",
                    "Overflow",
                    long.MaxValue,
                    long.MaxValue),
                new StageSeed(ids[2], StageItemKind.File, "C:\\Anchor", "Anchor", long.MaxValue));

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var source = before.Single(item => item.Id == ids[0]);
            var firstOther = before.First(item => item.Id != source.Id);
            await Assert.ThrowsAsync<OverflowException>(() =>
                repository.ReorderAsync(
                    new StageReorderRequest(
                        Version(source),
                        null,
                        Anchor(firstOther)),
                    TestContext.Current.CancellationToken));

            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReorderSqlFailurePropagatesAndRollsBack()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-sql-failure.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var sourceId = Guid.Parse("35000000-0000-0000-0000-000000000001");
            var anchorId = Guid.Parse("35000000-0000-0000-0000-000000000002");
            InsertStageItems(
                databasePath,
                new StageSeed(sourceId, StageItemKind.File, "C:\\Source", "Source", 0),
                new StageSeed(anchorId, StageItemKind.File, "C:\\Anchor", "Anchor", 10));
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TRIGGER reject_stage_reorder
                    AFTER UPDATE OF sort_order ON stage_items
                    BEGIN
                        SELECT RAISE(ABORT, 'reorder failure');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var items = before.ToDictionary(static item => item.Id);
            await Assert.ThrowsAsync<SqliteException>(() =>
                repository.ReorderAsync(
                    new StageReorderRequest(
                        Version(items[sourceId]),
                        null,
                        Anchor(items[anchorId])),
                    TestContext.Current.CancellationToken));

            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RebalanceLaterSqlFailureRollsBackEarlierMemberUpdates()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "rebalance-sql-failure.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 3)
                .Select(index => Guid.Parse(
                    $"36000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            InsertStageItems(
                databasePath,
                new StageSeed(ids[0], StageItemKind.File, "C:\\Source", "Source", long.MaxValue),
                new StageSeed(ids[1], StageItemKind.File, "C:\\Older", "Older", long.MaxValue),
                new StageSeed(ids[2], StageItemKind.File, "C:\\Reject", "Reject", long.MaxValue));
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TRIGGER reject_later_stage_rebalance
                    BEFORE UPDATE OF sort_order ON stage_items
                    WHEN OLD.id = '{ids[2]:D}'
                    BEGIN
                        SELECT RAISE(ABORT, 'later rebalance failure');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var source = before.Single(item => item.Id == ids[0]);
            var firstOther = before.First(item => item.Id != source.Id);
            await Assert.ThrowsAsync<SqliteException>(() =>
                repository.ReorderAsync(
                    new StageReorderRequest(
                        Version(source),
                        null,
                        Anchor(firstOther)),
                    TestContext.Current.CancellationToken));

            Assert.Equal(before, await LoadAllAsync(repository));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReorderNeverTouchesFileDirectoryOrShellLinkSources()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-source-safety.db");
        var filePath = Path.Combine(directory, "source.txt");
        var sourceDirectory = Path.Combine(directory, "source-directory");
        var directoryMarker = Path.Combine(sourceDirectory, "marker.bin");
        var shellLinkPath = Path.Combine(directory, "source.lnk");
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllTextAsync(
                filePath,
                "file-source",
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                directoryMarker,
                [1, 2, 3, 4],
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                shellLinkPath,
                [5, 6, 7, 8],
                TestContext.Current.CancellationToken);
            var fileSnapshot = await FileSnapshot.CreateAsync(filePath);
            var markerSnapshot = await FileSnapshot.CreateAsync(directoryMarker);
            var linkSnapshot = await FileSnapshot.CreateAsync(shellLinkPath);
            var directoryWriteUtc = Directory.GetLastWriteTimeUtc(sourceDirectory);

            await InitializeStageSchemaAsync(databasePath);
            var fileId = Guid.Parse("40000000-0000-0000-0000-000000000001");
            var directoryId = Guid.Parse("40000000-0000-0000-0000-000000000002");
            var linkId = Guid.Parse("40000000-0000-0000-0000-000000000003");
            InsertStageItems(
                databasePath,
                new StageSeed(
                    fileId,
                    StageItemKind.File,
                    filePath,
                    "File",
                    long.MaxValue),
                new StageSeed(
                    directoryId,
                    StageItemKind.Directory,
                    sourceDirectory,
                    "Directory",
                    long.MaxValue),
                new StageSeed(
                    linkId,
                    StageItemKind.ShellLink,
                    shellLinkPath,
                    "Link",
                    long.MaxValue));

            using var repository = new SqliteStageRepository(databasePath);
            await MoveToTopAsync(repository, fileId);

            await fileSnapshot.AssertUnchangedAsync();
            await markerSnapshot.AssertUnchangedAsync();
            await linkSnapshot.AssertUnchangedAsync();
            Assert.True(Directory.Exists(sourceDirectory));
            Assert.Equal(directoryWriteUtc, Directory.GetLastWriteTimeUtc(sourceDirectory));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task PageBoundariesTrackTheRowsImmediatelyOutsideEachPage()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "page-boundaries.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(1, 5)
                .Select(index => Guid.Parse(
                    $"00000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO stage_items VALUES
                    ('{ids[0]:D}', 0, 'C:\\A', 'C:\\A', 'A', 50, 0, 1, '{UtcText}', '{UtcText}'),
                    ('{ids[1]:D}', 0, 'C:\\B', 'C:\\B', 'B', 40, 0, 1, '{UtcText}', '{UtcText}'),
                    ('{ids[2]:D}', 0, 'C:\\C', 'C:\\C', 'C', 30, 0, 1, '{UtcText}', '{UtcText}'),
                    ('{ids[3]:D}', 0, 'C:\\D', 'C:\\D', 'D', 20, 0, 1, '{UtcText}', '{UtcText}'),
                    ('{ids[4]:D}', 0, 'C:\\E', 'C:\\E', 'E', 10, 0, 1, '{UtcText}', '{UtcText}');
                    """;
                command.ExecuteNonQuery();
            }

            using var repository = new SqliteStageRepository(databasePath);
            var first = await repository.LoadPageAsync(
                null,
                2,
                TestContext.Current.CancellationToken);
            var second = await repository.LoadPageAsync(
                first.NextCursor,
                2,
                TestContext.Current.CancellationToken);
            var last = await repository.LoadPageAsync(
                second.NextCursor,
                2,
                TestContext.Current.CancellationToken);
            var staleCursorPage = await repository.LoadPageAsync(
                new StagePageCursor(0, Guid.NewGuid()),
                2,
                TestContext.Current.CancellationToken);

            Assert.Equal(ids[..2], first.Items.Select(static item => item.Id));
            Assert.Null(first.NewerBoundary);
            Assert.Equal(new StageOrderAnchor(ids[2], 1, 30), first.OlderBoundary);
            Assert.Equal(
                new StagePageCursor(40, ids[1]),
                Assert.IsType<StagePageCursor>(first.NextCursor));

            Assert.Equal(ids[2..4], second.Items.Select(static item => item.Id));
            Assert.Equal(new StageOrderAnchor(ids[1], 1, 40), second.NewerBoundary);
            Assert.Equal(new StageOrderAnchor(ids[4], 1, 10), second.OlderBoundary);

            Assert.Equal(ids[4], Assert.Single(last.Items).Id);
            Assert.Equal(new StageOrderAnchor(ids[3], 1, 20), last.NewerBoundary);
            Assert.Null(last.OlderBoundary);
            Assert.Null(last.NextCursor);

            Assert.Empty(staleCursorPage.Items);
            Assert.Equal(5, staleCursorPage.TotalCount);
            Assert.Null(staleCursorPage.NewerBoundary);
            Assert.Null(staleCursorPage.OlderBoundary);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SingleItemPageHasNoOrderBoundaries()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "single-page-boundaries.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            InsertStageItems(
                databasePath,
                new StageSeed(
                    Guid.NewGuid(),
                    StageItemKind.File,
                    "C:\\Only",
                    "Only",
                    10));

            using var repository = new SqliteStageRepository(databasePath);
            var page = await repository.LoadPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);

            Assert.Single(page.Items);
            Assert.Null(page.NewerBoundary);
            Assert.Null(page.OlderBoundary);
            Assert.Null(page.NextCursor);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task AnchorComparisonMatchesSqliteForRandomEqualSortOrders()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "random-tie-order.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            var ids = Enumerable.Range(0, 128)
                .Select(static _ => Guid.NewGuid())
                .Distinct()
                .ToArray();
            Assert.Equal(128, ids.Length);
            InsertStageItems(
                databasePath,
                ids.Select((id, index) => new StageSeed(
                        id,
                        StageItemKind.File,
                        $"C:\\Tie\\{index:D3}",
                        index.ToString("D3", CultureInfo.InvariantCulture),
                        10))
                    .ToArray());

            using var repository = new SqliteStageRepository(databasePath);
            var actual = await LoadAllAsync(repository);
            var expectedIds = ids.OrderByDescending(
                static id => id.ToString("D"),
                StringComparer.Ordinal);

            Assert.Equal(expectedIds, actual.Select(static item => item.Id));
            Assert.All(
                actual.Zip(actual.Skip(1)),
                pair => Assert.True(
                    Anchor(pair.First).CompareTo(Anchor(pair.Second)) > 0));
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
            var firstOlderBoundary = Assert.IsType<StageOrderAnchor>(
                first.OlderBoundary);
            Assert.Equal(new StageOrderAnchor(lowerId, 1, 10), firstOlderBoundary);
            Assert.Equal(new StageOrderAnchor(higherId, 1, 10), second.NewerBoundary);
            Assert.True(first.Items[0].Id.ToString("D").CompareTo(
                firstOlderBoundary.Id.ToString("D")) > 0);
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
        var insertedIds = new Guid[itemCount];
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
                    var itemId = Guid.NewGuid();
                    insertedIds[index - 1] = itemId;
                    id.Value = itemId.ToString("D");
                    target.Value = path;
                    key.Value = path.ToUpperInvariant();
                    name.Value = index.ToString("D5", CultureInfo.InvariantCulture);
                    sortOrder.Value = index > itemCount - 63
                        ? long.MaxValue
                        : index * SqliteStageRepository.SortOrderStep;
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

                var topPage = await repository.LoadPageAsync(
                    null,
                    1,
                    TestContext.Current.CancellationToken);
                var top = Assert.Single(topPage.Items);
                var rebalanced = await repository.ReorderAsync(
                    new StageReorderRequest(
                        new StageItemVersion(insertedIds[0], 1),
                        null,
                        Anchor(top)),
                    TestContext.Current.CancellationToken);
                Assert.Equal(StageReorderStatus.Applied, rebalanced.Status);
                Assert.Equal(
                    StageReorderResult.MaximumChangedItems,
                    rebalanced.ChangedItems.Count);

                var afterRebalance = await repository.LoadPageAsync(
                    null,
                    StageReorderResult.MaximumChangedItems,
                    TestContext.Current.CancellationToken);
                Assert.Equal(insertedIds[0], afterRebalance.Items[0].Id);
                AssertStrictlyDescendingSortOrders(afterRebalance.Items);

                var fastPath = await repository.ReorderAsync(
                    new StageReorderRequest(
                        new StageItemVersion(insertedIds[1], 1),
                        null,
                        Anchor(afterRebalance.Items[0])),
                    TestContext.Current.CancellationToken);
                Assert.Equal(StageReorderStatus.Applied, fastPath.Status);
                Assert.Single(fastPath.ChangedItems);
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
            var details = new List<string>();
            using (var reader = planCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
            }

            Assert.Contains(details, detail =>
                detail.Contains("ix_stage_items_order", StringComparison.OrdinalIgnoreCase));

            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM stage_items
                WHERE sort_order > 9000000000
                   OR (sort_order = 9000000000 AND id > '00000000-0000-0000-0000-000000000000')
                ORDER BY sort_order ASC, id ASC
                LIMIT 1;
                """;
            details.Clear();
            using (var reader = planCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
            }

            Assert.Contains(details, detail =>
                detail.Contains("ix_stage_items_order", StringComparison.OrdinalIgnoreCase));

            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM stage_items
                WHERE id <> '00000000-0000-0000-0000-000000000001'
                  AND (sort_order < 9000000000
                       OR (sort_order = 9000000000
                           AND id < 'ffffffff-ffff-ffff-ffff-ffffffffffff'))
                ORDER BY sort_order DESC, id DESC
                LIMIT 1;
                """;
            details.Clear();
            using (var reader = planCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
            }

            Assert.Contains(details, detail =>
                detail.Contains("ix_stage_items_order", StringComparison.OrdinalIgnoreCase));

            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM stage_items
                WHERE id <> '00000000-0000-0000-0000-000000000001'
                  AND (sort_order > 9000000000
                       OR (sort_order = 9000000000
                           AND id >= '00000000-0000-0000-0000-000000000002'))
                ORDER BY sort_order ASC, id ASC
                LIMIT 64;
                """;
            details.Clear();
            using (var reader = planCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
            }

            Assert.Contains(details, detail =>
                detail.Contains("ix_stage_items_order", StringComparison.OrdinalIgnoreCase));

            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM stage_items
                WHERE id <> '00000000-0000-0000-0000-000000000001'
                  AND (sort_order < 9000000000
                       OR (sort_order = 9000000000
                           AND id <= 'ffffffff-ffff-ffff-ffff-ffffffffffff'))
                ORDER BY sort_order DESC, id DESC
                LIMIT 64;
                """;
            details.Clear();
            using (var reader = planCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    details.Add(reader.GetString(3));
                }
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
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.RelinkAsync(
                    Guid.Empty,
                    1,
                    Target("C:\\A", "A"),
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.RelinkAsync(
                    Guid.NewGuid(),
                    0,
                    Target("C:\\A", "A"),
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.RelinkAsync(
                    Guid.NewGuid(),
                    1,
                    Target("", "A"),
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                repository.ReorderAsync(
                    null!,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static void InsertStageItems(
        string databasePath,
        params StageSeed[] items)
    {
        using var connection = OpenDatabase(databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO stage_items (
                id, target_kind, target_value, canonical_key,
                resolved_name, sort_order, is_pinned, revision,
                created_utc, updated_utc)
            VALUES (
                $id, $kind, $target, $key,
                $name, $sortOrder, $isPinned, $revision,
                $utc, $utc);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var target = command.Parameters.Add("$target", SqliteType.Text);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var sortOrder = command.Parameters.Add("$sortOrder", SqliteType.Integer);
        var isPinned = command.Parameters.Add("$isPinned", SqliteType.Integer);
        var revision = command.Parameters.Add("$revision", SqliteType.Integer);
        command.Parameters.AddWithValue("$utc", UtcText);
        foreach (var item in items)
        {
            id.Value = item.Id.ToString("D");
            kind.Value = (int)item.Kind;
            target.Value = item.TargetValue;
            key.Value = item.TargetValue.ToUpperInvariant();
            name.Value = item.ResolvedName;
            sortOrder.Value = item.SortOrder;
            isPinned.Value = item.IsPinned ? 1 : 0;
            revision.Value = item.Revision;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static async Task<IReadOnlyList<StageItem>> LoadAllAsync(
        SqliteStageRepository repository) =>
        (await repository.LoadPageAsync(
            null,
            256,
            TestContext.Current.CancellationToken)).Items;

    private static StageItemVersion Version(StageItem item) =>
        new(item.Id, item.Revision);

    private static StageOrderAnchor Anchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);

    private static void AssertBusinessFieldsEqual(StageItem expected, StageItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.TargetValue, actual.TargetValue);
        Assert.Equal(expected.CanonicalKey, actual.CanonicalKey);
        Assert.Equal(expected.ResolvedName, actual.ResolvedName);
        Assert.Equal(expected.IsPinned, actual.IsPinned);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);
    }

    private static async Task MoveToTopAsync(
        SqliteStageRepository repository,
        Guid sourceId)
    {
        var items = await LoadAllAsync(repository);
        var source = items.Single(item => item.Id == sourceId);
        var firstOther = items.First(item => item.Id != sourceId);
        var result = await repository.ReorderAsync(
            new StageReorderRequest(
                Version(source),
                null,
                Anchor(firstOther)),
            TestContext.Current.CancellationToken);
        Assert.Equal(StageReorderStatus.Applied, result.Status);
    }

    private static async Task AssertReorderAppliedAsync(
        StageSeed[] seeds,
        Guid sourceId,
        Guid? newerId,
        Guid? olderId,
        IReadOnlyList<Guid> expectedOrder)
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "reorder-rebalanced.db");
        try
        {
            await InitializeStageSchemaAsync(databasePath);
            InsertStageItems(databasePath, seeds);
            using var repository = new SqliteStageRepository(databasePath);
            var before = await LoadAllAsync(repository);
            var items = before.ToDictionary(static item => item.Id);
            StageOrderAnchor? newer = newerId is { } presentNewerId
                ? Anchor(items[presentNewerId])
                : null;
            StageOrderAnchor? older = olderId is { } presentOlderId
                ? Anchor(items[presentOlderId])
                : null;

            var result = await repository.ReorderAsync(
                new StageReorderRequest(
                    Version(items[sourceId]),
                    newer,
                    older),
                TestContext.Current.CancellationToken);

            var after = await LoadAllAsync(repository);
            Assert.Equal(StageReorderStatus.Applied, result.Status);
            Assert.InRange(
                result.ChangedItems.Count,
                1,
                StageReorderResult.MaximumChangedItems);
            Assert.Equal(expectedOrder, after.Select(static item => item.Id));
            AssertStrictlyDescendingSortOrders(after);

            var originals = before.ToDictionary(static item => item.Id);
            Assert.All(after, item => AssertBusinessFieldsEqual(originals[item.Id], item));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static StageSeed[] CreateDenseStageSeeds(int itemCount) =>
        Enumerable.Range(1, itemCount)
            .Select(index => new StageSeed(
                Guid.Parse($"21000000-0000-0000-0000-{index:D12}"),
                StageItemKind.File,
                $"C:\\Dense\\{index:D3}.txt",
                $"{index:D3}.txt",
                long.MaxValue))
            .ToArray();

    private static void AssertStrictlyDescendingSortOrders(
        IReadOnlyList<StageItem> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            Assert.True(items[index - 1].SortOrder > items[index].SortOrder);
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

    private sealed record FileSnapshot(
        string Path,
        long Length,
        DateTime LastWriteUtc,
        byte[] Hash)
    {
        public static async Task<FileSnapshot> CreateAsync(string path) => new(
            path,
            new FileInfo(path).Length,
            File.GetLastWriteTimeUtc(path),
            SHA256.HashData(await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken)));

        public async Task AssertUnchangedAsync()
        {
            Assert.True(File.Exists(Path));
            Assert.Equal(Length, new FileInfo(Path).Length);
            Assert.Equal(LastWriteUtc, File.GetLastWriteTimeUtc(Path));
            Assert.Equal(
                Hash,
                SHA256.HashData(await File.ReadAllBytesAsync(
                    Path,
                    TestContext.Current.CancellationToken)));
        }
    }

    private sealed record StageSeed(
        Guid Id,
        StageItemKind Kind,
        string TargetValue,
        string ResolvedName,
        long SortOrder,
        long Revision = 1,
        bool IsPinned = false);

    private const string UtcText = "2026-09-04T00:00:00.0000000+00:00";
}
