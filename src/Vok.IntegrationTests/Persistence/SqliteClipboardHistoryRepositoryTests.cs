using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class SqliteClipboardHistoryRepositoryTests
{
    [Fact]
    public async Task SchemaTwoUpgradesToCurrentWithoutLosingLauncherRows()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "upgrade.db");
        try
        {
            CreateSchemaTwoDatabase(databasePath);

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                Assert.Equal(
                    0,
                    (await repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken)).TotalCount);
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(
                "keep-launcher",
                ExecuteString(
                    connection,
                    "SELECT resolved_name FROM launcher_items WHERE id = '00000000-0000-0000-0000-000000000001';"));
            Assert.Equal(1L, TableExists(connection, "clipboard_entries"));
            Assert.Equal(1L, TableExists(connection, "clipboard_representations"));
            Assert.Equal(1L, TableExists(connection, "clipboard_files"));
            Assert.Equal(1L, TableExists(connection, "content_assets"));
            Assert.Equal(1L, TableExists(connection, "clipboard_asset_refs"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaThreeMigratesTransactionallyAndPreservesTextReplay()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "schema3.db");
        try
        {
            CreateSchemaThreeDatabase(databasePath);

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var page = await repository.LoadPageAsync(
                    null,
                    32,
                    TestContext.Current.CancellationToken);
                var entry = Assert.Single(page.Items);
                Assert.Null(entry.FileIntent);
                Assert.Equal(0, entry.ItemCount);
                var replay = await repository.GetReplayPayloadAsync(
                    entry.Id,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(replay);
                Assert.Equal("preserved", replay.PlainText);
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(1L, TableExists(connection, "clipboard_files"));
            Assert.Equal(
                0L,
                ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_files;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaThreeWithBrokenKeysFailsAndRollsBackMigration()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "broken-schema3.db");
        try
        {
            CreateSchemaThreeDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA foreign_keys = OFF;
                    ALTER TABLE clipboard_representations
                    RENAME TO clipboard_representations_valid;
                    CREATE TABLE clipboard_representations (
                        entry_id TEXT NOT NULL,
                        representation_kind INTEGER NOT NULL,
                        fidelity_rank INTEGER NOT NULL,
                        inline_text TEXT NOT NULL,
                        byte_length INTEGER NOT NULL,
                        PRIMARY KEY (entry_id, representation_kind)
                    );
                    INSERT INTO clipboard_representations
                    SELECT * FROM clipboard_representations_valid;
                    DROP TABLE clipboard_representations_valid;
                    DROP INDEX ux_clipboard_entries_semantic;
                    CREATE UNIQUE INDEX ux_clipboard_entries_semantic
                    ON clipboard_entries(semantic_hash);
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken));
                Assert.Contains("foreign keys or indexes", exception.Message);
            }

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(3L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(0L, TableExists(reopened, "clipboard_files"));
            Assert.Equal(0L, TableExists(reopened, "content_assets"));
            Assert.Equal(0L, TableExists(reopened, "clipboard_asset_refs"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_table_info('clipboard_entries') WHERE name IN ('file_intent', 'item_count');"));
            Assert.Equal(
                "preserved",
                ExecuteString(
                    reopened,
                    "SELECT inline_text FROM clipboard_representations LIMIT 1;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaFourMigratesToCurrentAndKeepsExistingRowsImageColumnsNull()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "schema4.db");
        try
        {
            CreateSchemaFourDatabase(databasePath);

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var page = await repository.LoadPageAsync(
                    null,
                    32,
                    TestContext.Current.CancellationToken);
                Assert.Equal(2, page.Items.Count);
                var entry = Assert.Single(
                    page.Items,
                    static item => item.Kind is ClipboardContentKind.Text);
                var fileEntry = Assert.Single(
                    page.Items,
                    static item => item.Kind is ClipboardContentKind.FileList);
                Assert.Null(entry.Image);
                Assert.Null(fileEntry.Image);
                Assert.Equal(
                    "preserved",
                    (await repository.GetReplayPayloadAsync(
                        entry.Id,
                        TestContext.Current.CancellationToken))?.PlainText);
                var fileReplay = await repository.GetReplayPayloadAsync(
                    fileEntry.Id,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(fileReplay);
                Assert.Equal(
                    Path.Combine(directory, "preserved-file.txt"),
                    Assert.Single(fileReplay.FileReferences).CanonicalPath);
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(1L, TableExists(connection, "content_assets"));
            Assert.Equal(1L, TableExists(connection, "clipboard_asset_refs"));
            Assert.Equal(
                5L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('clipboard_entries') WHERE name IN ('pixel_width', 'pixel_height', 'has_alpha', 'dpi_x_milli', 'dpi_y_milli');"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('clipboard_entries') WHERE name = 'total_bytes';"));
            Assert.Equal(
                2L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM clipboard_entries WHERE total_bytes > 0;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM clipboard_entries WHERE pixel_width IS NOT NULL OR pixel_height IS NOT NULL OR has_alpha IS NOT NULL OR dpi_x_milli IS NOT NULL OR dpi_y_milli IS NOT NULL;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task HalfMigratedSchemaFourIsRejectedWithoutFurtherMutation()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "half-schema4.db");
        try
        {
            CreateSchemaFourDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    ALTER TABLE clipboard_entries
                    ADD COLUMN pixel_width INTEGER NULL;
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken));
                Assert.Contains("unsupported schema", exception.Message);
            }

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(4L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(0L, TableExists(reopened, "content_assets"));
            Assert.Equal(0L, TableExists(reopened, "clipboard_asset_refs"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_table_info('clipboard_entries') WHERE name IN ('pixel_width', 'pixel_height', 'has_alpha', 'dpi_x_milli', 'dpi_y_milli');"));
            Assert.Equal(
                "preserved",
                ExecuteString(
                    reopened,
                    "SELECT inline_text FROM clipboard_representations LIMIT 1;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaFiveWithBrokenAssetForeignKeyRollsBackSixMigration()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "broken-schema5.db");
        try
        {
            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                _ = await repository.LoadPageAsync(
                    null,
                    32,
                    TestContext.Current.CancellationToken);
            }

            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA foreign_keys = OFF;
                    ALTER TABLE clipboard_entries DROP COLUMN total_bytes;
                    PRAGMA user_version = 5;
                    DROP TABLE clipboard_asset_refs;
                    CREATE TABLE clipboard_asset_refs (
                        entry_id TEXT NOT NULL,
                        role INTEGER NOT NULL,
                        asset_hash TEXT NOT NULL,
                        PRIMARY KEY (entry_id, role),
                        FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
                    );
                    CREATE INDEX ix_clipboard_asset_refs_hash
                    ON clipboard_asset_refs(asset_hash);
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken));
                Assert.Contains("foreign keys or indexes", exception.Message);
            }

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(5L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_table_info('clipboard_entries') WHERE name = 'total_bytes';"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_foreign_key_list('clipboard_asset_refs');"));
            Assert.Equal(1L, TableExists(reopened, "content_assets"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task FileListsPersistDeduplicateByIntentAndNeverMutateSourceFiles()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "files.db");
        var sourcePath = Path.Combine(directory, "source.txt");
        try
        {
            File.WriteAllText(sourcePath, "source-content");
            var copyContent = FileList(
                ClipboardFileTransferIntent.Copy,
                sourcePath);
            var moveContent = FileList(
                ClipboardFileTransferIntent.Move,
                sourcePath);
            Guid copyId;
            Guid moveId;
            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var copy = await repository.UpsertFileListAsync(
                    copyContent,
                    DateTimeOffset.UnixEpoch,
                    TestContext.Current.CancellationToken);
                var repeated = await repository.UpsertFileListAsync(
                    copyContent,
                    DateTimeOffset.UnixEpoch.AddMinutes(1),
                    TestContext.Current.CancellationToken);
                var move = await repository.UpsertFileListAsync(
                    moveContent,
                    DateTimeOffset.UnixEpoch.AddMinutes(2),
                    TestContext.Current.CancellationToken);

                Assert.Equal(copy.Id, repeated.Id);
                Assert.Equal(2, repeated.CaptureCount);
                Assert.NotEqual(copy.Id, move.Id);
                Assert.Equal(ClipboardFileTransferIntent.Move, move.FileIntent);
                Assert.Equal(1, move.ItemCount);
                copyId = copy.Id;
                moveId = move.Id;
            }

            using (var reopened = new SqliteClipboardHistoryRepository(databasePath))
            {
                var copyReplay = await reopened.GetReplayPayloadAsync(
                    copyId,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(copyReplay);
                Assert.Equal(ClipboardContentKind.FileList, copyReplay.Kind);
                Assert.Equal(ClipboardFileTransferIntent.Copy, copyReplay.FileIntent);
                Assert.Equal(sourcePath, Assert.Single(copyReplay.FileReferences).CanonicalPath);

                Assert.True(await reopened.RemoveAsync(
                    copyId,
                    TestContext.Current.CancellationToken));
                Assert.True(File.Exists(sourcePath));
                Assert.Equal("source-content", File.ReadAllText(sourcePath));

                Assert.True(await reopened.RemoveAsync(
                    moveId,
                    TestContext.Current.CancellationToken));
                Assert.True(File.Exists(sourcePath));
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_files;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptedRelativeFileReplayIsRejectedAsAWhole()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "corrupt-file.db");
        try
        {
            Guid id;
            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                id = (await repository.UpsertFileListAsync(
                    FileList(
                        ClipboardFileTransferIntent.Copy,
                        Path.Combine(directory, "valid.txt")),
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken)).Id;
            }

            const string relativePath = "relative.txt";
            var corruptHash = ClipboardFileSemanticIdentity.Compute(
                ClipboardFileTransferIntent.Copy,
                [new ClipboardFileReference(0, relativePath, relativePath, relativePath)]);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE clipboard_entries
                    SET semantic_hash = $hash
                    WHERE id = $id;
                    UPDATE clipboard_files
                    SET original_path = $path,
                        canonical_path = $path,
                        display_name = $path
                    WHERE entry_id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$hash", corruptHash);
                command.Parameters.AddWithValue("$path", relativePath);
                command.ExecuteNonQuery();
            }

            using var reopened = new SqliteClipboardHistoryRepository(databasePath);
            Assert.Null(await reopened.GetReplayPayloadAsync(
                id,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task DuplicateContentMovesToFrontAndPreservesPin()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(
                Path.Combine(directory, "dedup.db"));
            var first = await repository.UpsertTextAsync(
                Text("alpha", Hash(1)),
                DateTimeOffset.Parse("2026-08-28T01:00:00Z", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);
            var second = await repository.UpsertTextAsync(
                Text("beta", Hash(2)),
                DateTimeOffset.Parse("2026-08-28T02:00:00Z", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);
            var pinned = await repository.SetPinnedAsync(
                first.Id,
                true,
                TestContext.Current.CancellationToken);
            var repeated = await repository.UpsertTextAsync(
                Text("alpha", Hash(1)),
                DateTimeOffset.Parse("2026-08-28T03:00:00Z", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);

            var page = await repository.LoadPageAsync(
                null,
                32,
                TestContext.Current.CancellationToken);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal([repeated.Id, second.Id], page.Items.Select(static item => item.Id));
            Assert.Equal(first.Id, repeated.Id);
            Assert.Equal(2, repeated.CaptureCount);
            Assert.True(repeated.IsPinned);
            Assert.Equal(3, repeated.Revision);
            Assert.NotNull(pinned);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task KeysetPagingLoadsOnlyRequestedPagesInNewestOrder()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(
                Path.Combine(directory, "paging.db"));
            for (var index = 0; index < 130; index++)
            {
                await repository.UpsertTextAsync(
                    Text($"text-{index}", Hash(index + 1)),
                    DateTimeOffset.UnixEpoch.AddSeconds(index),
                    TestContext.Current.CancellationToken);
            }

            var first = await repository.LoadPageAsync(
                null,
                64,
                TestContext.Current.CancellationToken);
            var second = await repository.LoadPageAsync(
                first.NextCursor,
                64,
                TestContext.Current.CancellationToken);
            var third = await repository.LoadPageAsync(
                second.NextCursor,
                64,
                TestContext.Current.CancellationToken);

            Assert.Equal(130, first.TotalCount);
            Assert.Equal(64, first.Items.Count);
            Assert.Equal(64, second.Items.Count);
            Assert.Equal(2, third.Items.Count);
            Assert.Equal("text-129", first.Items[0].PlainPreview);
            Assert.Equal("text-0", third.Items[^1].PlainPreview);
            Assert.Null(third.NextCursor);
            Assert.Equal(
                130,
                first.Items.Concat(second.Items).Concat(third.Items)
                    .Select(static item => item.Id)
                    .Distinct()
                    .Count());
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RetentionExpiresAndLimitsOnlyUnpinnedEntries()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(
                Path.Combine(directory, "retention.db"));
            var pinnedOld = await repository.UpsertTextAsync(
                Text("pinned-old", Hash(201)),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            var expired = await repository.UpsertTextAsync(
                Text("expired", Hash(202)),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            var overflow = await repository.UpsertTextAsync(
                Text("overflow", Hash(203)),
                new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            var recent = await repository.UpsertTextAsync(
                Text("recent", Hash(204)),
                new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            var newest = await repository.UpsertTextAsync(
                Text("newest", Hash(205)),
                new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            _ = await repository.SetPinnedAsync(
                pinnedOld.Id,
                true,
                TestContext.Current.CancellationToken);

            var result = await repository.ApplyRetentionAsync(
                new ClipboardRetentionOptions(
                    maximumUnpinnedEntryCount: 2,
                    maximumAge: TimeSpan.FromDays(3),
                    maximumTotalBytes: long.MaxValue),
                new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);

            Assert.Equal(3, result.RemainingEntryCount);
            Assert.Equal(
                [expired.Id, overflow.Id],
                result.RemovedEntryIds);
            var page = await repository.LoadPageAsync(
                null,
                32,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                [newest.Id, recent.Id, pinnedOld.Id],
                page.Items.Select(static item => item.Id));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task TotalByteProtectionEvictsUnpinnedBeforePinnedEntries()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteClipboardHistoryRepository(
                Path.Combine(directory, "byte-retention.db"));
            var pinnedOld = await repository.UpsertTextAsync(
                Text("aaaaaaaaaa", Hash(211)),
                DateTimeOffset.UnixEpoch,
                TestContext.Current.CancellationToken);
            var unpinned = await repository.UpsertTextAsync(
                Text("bbbbbbbbbb", Hash(212)),
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                TestContext.Current.CancellationToken);
            var pinnedNew = await repository.UpsertTextAsync(
                Text("cccccccccc", Hash(213)),
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                TestContext.Current.CancellationToken);
            _ = await repository.SetPinnedAsync(
                pinnedOld.Id,
                true,
                TestContext.Current.CancellationToken);
            _ = await repository.SetPinnedAsync(
                pinnedNew.Id,
                true,
                TestContext.Current.CancellationToken);

            var result = await repository.ApplyRetentionAsync(
                new ClipboardRetentionOptions(
                    maximumUnpinnedEntryCount: 10,
                    maximumAge: ClipboardRetentionOptions.MaximumSupportedAge,
                    maximumTotalBytes: 64),
                DateTimeOffset.UnixEpoch.AddDays(1),
                TestContext.Current.CancellationToken);

            Assert.Equal([unpinned.Id], result.RemovedEntryIds);
            Assert.Equal(64, result.ManagedBytesAfter);
            var page = await repository.LoadPageAsync(
                null,
                32,
                TestContext.Current.CancellationToken);
            Assert.Equal([pinnedNew.Id, pinnedOld.Id], page.Items.Select(static item => item.Id));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task TenThousandMetadataRowsRemainKeysetPagedAndRetentionBounded()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "ten-thousand.db");
        try
        {
            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                _ = await repository.LoadPageAsync(
                    null,
                    1,
                    TestContext.Current.CancellationToken);
            }

            using (var connection = OpenDatabase(databasePath))
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO clipboard_entries (
                        id, kind, semantic_hash, plain_preview,
                        first_captured_utc, last_captured_utc,
                        last_capture_order, capture_count, is_pinned, revision,
                        file_intent, item_count, pixel_width, pixel_height,
                        has_alpha, dpi_x_milli, dpi_y_milli, total_bytes)
                    VALUES (
                        $id, 0, $hash, $preview,
                        $utc, $utc, $captureOrder, 1, 0, 1,
                        NULL, 0, NULL, NULL, NULL, NULL, NULL, 1);
                    """;
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var hash = command.Parameters.Add("$hash", SqliteType.Text);
                var preview = command.Parameters.Add("$preview", SqliteType.Text);
                var utc = command.Parameters.Add("$utc", SqliteType.Text);
                var captureOrder = command.Parameters.Add("$captureOrder", SqliteType.Integer);
                for (var index = 0; index < 10_000; index++)
                {
                    id.Value = Guid.NewGuid().ToString("D");
                    hash.Value = Hash(index + 1);
                    preview.Value = "x";
                    utc.Value = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddSeconds(index)
                        .ToString("O", CultureInfo.InvariantCulture);
                    captureOrder.Value = index + 1;
                    _ = command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            using var reopened = new SqliteClipboardHistoryRepository(databasePath);
            var first = await reopened.LoadPageAsync(
                null,
                64,
                TestContext.Current.CancellationToken);
            Assert.Equal(10_000, first.TotalCount);
            Assert.Equal(64, first.Items.Count);
            Assert.NotNull(first.NextCursor);

            var maintenance = await reopened.ApplyRetentionAsync(
                new ClipboardRetentionOptions(
                    maximumUnpinnedEntryCount: 10_000,
                    maximumAge: ClipboardRetentionOptions.MaximumSupportedAge,
                    maximumTotalBytes: long.MaxValue),
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken);
            Assert.Empty(maintenance.RemovedEntryIds);
            Assert.Equal(10_000, maintenance.RemainingEntryCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task ReplayPersistsAcrossRestartAndDeleteCascadesRepresentations()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "replay.db");
        try
        {
            Guid id;
            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                id = (await repository.UpsertTextAsync(
                    RichText("hello", Hash(7)),
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken)).Id;
            }

            using (var reopened = new SqliteClipboardHistoryRepository(databasePath))
            {
                var replay = await reopened.GetReplayPayloadAsync(
                    id,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(replay);
                Assert.Equal("hello", replay.PlainText);
                Assert.Equal(3, replay.Representations.Count);
                Assert.True(await reopened.RemoveAsync(
                    id,
                    TestContext.Current.CancellationToken));
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_entries;"));
            Assert.Equal(0L, ExecuteInt64(connection, "SELECT COUNT(*) FROM clipboard_representations;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task UnknownClipboardSchemaFailsWithoutReplacingUserTable()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "unknown.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE clipboard_entries (
                        id TEXT PRIMARY KEY,
                        sentinel TEXT NOT NULL
                    );
                    INSERT INTO clipboard_entries (id, sentinel) VALUES ('keep', 'user-data');
                    PRAGMA user_version = 2;
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteClipboardHistoryRepository(databasePath))
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken));
                Assert.Contains("unsupported schema", exception.Message);
            }

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(
                "user-data",
                ExecuteString(reopened, "SELECT sentinel FROM clipboard_entries WHERE id = 'keep';"));
            Assert.Equal(2L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(0L, TableExists(reopened, "clipboard_representations"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static ClipboardTextContent Text(string value, string hash) => new(
        hash,
        value,
        value,
        [new ClipboardStoredRepresentation(
            ClipboardRepresentationKind.UnicodeText,
            100,
            value,
            (value.Length * 2) + 2)]);

    private static ClipboardTextContent RichText(string value, string hash) => new(
        hash,
        value,
        value,
        [
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Html,
                300,
                $"<b>{value}</b>",
                value.Length + 7),
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Rtf,
                200,
                $"{{\\rtf1 {value}}}",
                value.Length + 8),
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.UnicodeText,
                100,
                value,
                (value.Length * 2) + 2),
        ]);

    private static ClipboardFileListContent FileList(
        ClipboardFileTransferIntent intent,
        params string[] paths)
    {
        var files = paths.Select((path, index) => new ClipboardFileReference(
                index,
                path,
                Path.GetFullPath(path),
                Path.GetFileName(path)))
            .ToArray();
        return new ClipboardFileListContent(
            ClipboardFileSemanticIdentity.Compute(intent, files),
            intent,
            files.Length == 1
                ? files[0].DisplayName
                : $"{files[0].DisplayName} + 另外 {files.Length - 1} 项",
            files);
    }

    private static string Hash(int value) => value.ToString("x64", CultureInfo.InvariantCulture);

    private static void CreateSchemaTwoDatabase(string databasePath)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE launcher_items (
                id TEXT NOT NULL PRIMARY KEY,
                target_kind INTEGER NOT NULL,
                target_value TEXT NOT NULL,
                canonical_key TEXT NOT NULL COLLATE NOCASE UNIQUE,
                resolved_name TEXT NOT NULL,
                custom_name TEXT NULL,
                sort_order INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX ix_launcher_items_sort ON launcher_items(sort_order, id);
            INSERT INTO launcher_items (
                id, target_kind, target_value, canonical_key,
                resolved_name, custom_name, sort_order, revision,
                created_utc, updated_utc)
            VALUES (
                '00000000-0000-0000-0000-000000000001', 0, 'C:\keep.exe', '0:C:\KEEP.EXE',
                'keep-launcher', NULL, 0, 1,
                '2026-08-28T00:00:00.0000000+00:00', '2026-08-28T00:00:00.0000000+00:00');
            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateSchemaThreeDatabase(string databasePath)
    {
        CreateSchemaTwoDatabase(databasePath);
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE clipboard_entries (
                id TEXT NOT NULL PRIMARY KEY,
                kind INTEGER NOT NULL,
                semantic_hash TEXT NOT NULL,
                plain_preview TEXT NOT NULL,
                first_captured_utc TEXT NOT NULL,
                last_captured_utc TEXT NOT NULL,
                last_capture_order INTEGER NOT NULL,
                capture_count INTEGER NOT NULL,
                is_pinned INTEGER NOT NULL,
                revision INTEGER NOT NULL
            );
            CREATE TABLE clipboard_representations (
                entry_id TEXT NOT NULL,
                representation_kind INTEGER NOT NULL,
                fidelity_rank INTEGER NOT NULL,
                inline_text TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                PRIMARY KEY (entry_id, representation_kind),
                FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX ux_clipboard_entries_semantic
            ON clipboard_entries(kind, semantic_hash);
            CREATE INDEX ix_clipboard_entries_capture_order
            ON clipboard_entries(last_capture_order DESC, id DESC);
            INSERT INTO clipboard_entries VALUES (
                '00000000-0000-0000-0000-000000000002', 0, '{Hash(77)}', 'preserved',
                '2026-08-28T00:00:00.0000000+00:00',
                '2026-08-28T00:00:00.0000000+00:00', 1, 1, 0, 1);
            INSERT INTO clipboard_representations VALUES (
                '00000000-0000-0000-0000-000000000002', 1, 100, 'preserved', 20);
            PRAGMA user_version = 3;
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateSchemaFourDatabase(string databasePath)
    {
        CreateSchemaThreeDatabase(databasePath);
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE clipboard_entries
            ADD COLUMN file_intent INTEGER NULL;

            ALTER TABLE clipboard_entries
            ADD COLUMN item_count INTEGER NOT NULL DEFAULT 0;

            CREATE TABLE clipboard_files (
                entry_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                original_path TEXT NOT NULL,
                canonical_path TEXT NOT NULL,
                display_name TEXT NOT NULL,
                PRIMARY KEY (entry_id, ordinal),
                FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
            );

            PRAGMA user_version = 4;
            """;
        command.ExecuteNonQuery();

        var filePath = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            "preserved-file.txt");
        var files = new[]
        {
            new ClipboardFileReference(
                0,
                filePath,
                filePath,
                Path.GetFileName(filePath)),
        };
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO clipboard_entries (
                id, kind, semantic_hash, plain_preview,
                first_captured_utc, last_captured_utc,
                last_capture_order, capture_count, is_pinned, revision,
                file_intent, item_count)
            VALUES (
                '00000000-0000-0000-0000-000000000003', 2, $hash, 'preserved-file.txt',
                '2026-08-28T00:00:00.0000000+00:00',
                '2026-08-28T00:00:00.0000000+00:00', 2, 1, 0, 1, 1, 1);

            INSERT INTO clipboard_files (
                entry_id, ordinal, original_path, canonical_path, display_name)
            VALUES (
                '00000000-0000-0000-0000-000000000003', 0, $path, $path, 'preserved-file.txt');
            """;
        insert.Parameters.AddWithValue(
            "$hash",
            ClipboardFileSemanticIdentity.Compute(
                ClipboardFileTransferIntent.Copy,
                files));
        insert.Parameters.AddWithValue("$path", filePath);
        insert.ExecuteNonQuery();
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

    private static long TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ExecuteInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ExecuteString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
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
}
