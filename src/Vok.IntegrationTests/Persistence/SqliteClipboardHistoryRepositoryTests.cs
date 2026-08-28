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
    public async Task SchemaTwoUpgradesToThreeWithoutLosingLauncherRows()
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
            Assert.Equal(3L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(
                "keep-launcher",
                ExecuteString(
                    connection,
                    "SELECT resolved_name FROM launcher_items WHERE id = '00000000-0000-0000-0000-000000000001';"));
            Assert.Equal(1L, TableExists(connection, "clipboard_entries"));
            Assert.Equal(1L, TableExists(connection, "clipboard_representations"));
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
