using System.IO;
using Microsoft.Data.Sqlite;
using Vok.Core.Launcher;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class SqliteLauncherRepositoryTests
{
    [Fact]
    public async Task LegacySchemaMigratesTransactionallyAndRemainsIdempotent()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "legacy.db");
        try
        {
            CreateLegacyDatabase(databasePath);

            using (var repository = new SqliteLauncherRepository(databasePath))
            {
                var page = await repository.LoadPageAsync(
                    null,
                    32,
                    TestContext.Current.CancellationToken);

                Assert.Collection(
                    page.Items,
                    item =>
                    {
                        Assert.Equal(LauncherItemKind.File, item.Kind);
                        Assert.Equal(@"C:\Tools\Alpha.exe", item.TargetValue);
                        Assert.Equal("Alpha", item.DisplayName);
                        Assert.Equal(0, item.SortOrder);
                    },
                    item =>
                    {
                        Assert.Equal(LauncherItemKind.ShellLink, item.Kind);
                        Assert.Equal(@"C:\Tools\Beta.lnk", item.TargetValue);
                        Assert.Equal("Beta", item.DisplayName);
                        Assert.Equal(1, item.SortOrder);
                    });

                var duplicate = await repository.AddAsync(
                    [Target(@"c:\tools\BETA.LNK", "Duplicate", LauncherItemKind.ShellLink)],
                    TestContext.Current.CancellationToken);
                Assert.Empty(duplicate.Added);
                Assert.Single(duplicate.Existing);
            }

            using (var reopened = new SqliteLauncherRepository(databasePath))
            {
                Assert.Equal(
                    2,
                    (await reopened.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken)).TotalCount);
            }

            using var connection = OpenDatabase(databasePath);
            Assert.Equal(6L, ExecuteInt64(connection, "PRAGMA user_version;"));
            Assert.Equal(
                [
                    "id", "target_kind", "target_value", "canonical_key",
                    "resolved_name", "custom_name", "sort_order", "revision",
                    "created_utc", "updated_utc",
                ],
                ReadColumnNames(connection, "launcher_items"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'launcher_items_legacy_v1';"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_launcher_items_order';"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_launcher_items_sort';"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task UnknownLauncherSchemaFailsWithoutReplacingUserData()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "unknown.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE launcher_items (
                        id INTEGER PRIMARY KEY,
                        sentinel TEXT NOT NULL
                    );
                    INSERT INTO launcher_items (id, sentinel) VALUES (1, 'keep-me');
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteLauncherRepository(databasePath))
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
                "keep-me",
                ExecuteString(
                    reopened,
                    "SELECT sentinel FROM launcher_items WHERE id = 1;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task FailedLegacyCopyRollsBackTableRenameAndVersionChange()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "legacy-conflict.db");
        try
        {
            CreateLegacyDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE launcher_items
                    SET normalized_path = 'c:\tools\ALPHA.EXE',
                        path_key = 'A-DIFFERENT-LEGACY-KEY',
                        kind = 1
                    WHERE id = 2;
                    """;
                command.ExecuteNonQuery();
            }

            using (var repository = new SqliteLauncherRepository(databasePath))
            {
                await Assert.ThrowsAsync<SqliteException>(
                    () => repository.LoadPageAsync(
                        null,
                        32,
                        TestContext.Current.CancellationToken));
            }

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(1L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                [
                    "id", "normalized_path", "path_key", "display_name",
                    "kind", "sort_order", "created_utc",
                ],
                ReadColumnNames(reopened, "launcher_items"));
            Assert.Equal(2L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM launcher_items;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'launcher_items_legacy_v1';"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task AddedTargetsPersistInStableOrderAcrossRepositoryRestart()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "launcher.db");
        try
        {
            Guid firstId;
            using (var repository = new SqliteLauncherRepository(databasePath))
            {
                var result = await repository.AddAsync(
                    [Target("C:\\Tools\\Alpha.exe", "Alpha"), Target("C:\\Data", "Data", LauncherItemKind.Directory)],
                    TestContext.Current.CancellationToken);
                Assert.Equal(2, result.Added.Count);
                firstId = result.Added[0].Id;
            }

            using var reopened = new SqliteLauncherRepository(databasePath);
            var page = await reopened.LoadPageAsync(
                afterSortOrder: null,
                pageSize: 32,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, page.TotalCount);
            Assert.Collection(
                page.Items,
                item =>
                {
                    Assert.Equal(firstId, item.Id);
                    Assert.Equal("Alpha", item.DisplayName);
                    Assert.Equal(0, item.SortOrder);
                },
                item =>
                {
                    Assert.Equal("Data", item.DisplayName);
                    Assert.Equal(1, item.SortOrder);
                });
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CanonicalIdentityDeduplicatesAcrossBatchAndDatabaseCaseInsensitively()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteLauncherRepository(
                Path.Combine(directory, "dedup.db"));
            var first = await repository.AddAsync(
                [Target("C:\\Tools\\Alpha.exe", "Alpha")],
                TestContext.Current.CancellationToken);
            var duplicate = await repository.AddAsync(
                [
                    Target("c:\\tools\\ALPHA.EXE", "Duplicate"),
                    Target("C:\\TOOLS\\alpha.exe", "Duplicate again"),
                ],
                TestContext.Current.CancellationToken);

            Assert.Single(first.Added);
            Assert.Empty(duplicate.Added);
            Assert.Single(duplicate.Existing);
            Assert.Equal(first.Added[0].Id, duplicate.Existing[0].Id);
            Assert.Equal(
                1,
                (await repository.LoadPageAsync(
                    null,
                    32,
                    TestContext.Current.CancellationToken)).TotalCount);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task RemovingReferenceNeverDeletesTheUserTarget()
    {
        var directory = CreateTestDirectory();
        var userFile = Path.Combine(directory, "keep-me.txt");
        await File.WriteAllTextAsync(
            userFile,
            "user data",
            TestContext.Current.CancellationToken);
        try
        {
            using var repository = new SqliteLauncherRepository(
                Path.Combine(directory, "remove.db"));
            var added = await repository.AddAsync(
                [Target(userFile, "keep-me")],
                TestContext.Current.CancellationToken);

            Assert.True(await repository.RemoveAsync(
                added.Added[0].Id,
                TestContext.Current.CancellationToken));

            Assert.True(File.Exists(userFile));
            Assert.Equal(
                "user data",
                await File.ReadAllTextAsync(
                    userFile,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task PagingAndMoveUsePersistedSortOrder()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var repository = new SqliteLauncherRepository(
                Path.Combine(directory, "move.db"));
            var added = await repository.AddAsync(
                [Target("C:\\A.exe", "A"), Target("C:\\B.exe", "B"), Target("C:\\C.exe", "C")],
                TestContext.Current.CancellationToken);
            Assert.True(await repository.MoveToSlotAsync(
                added.Added[2].Id,
                slot: 0,
                TestContext.Current.CancellationToken));

            var firstPage = await repository.LoadPageAsync(
                null,
                2,
                TestContext.Current.CancellationToken);
            var secondPage = await repository.LoadPageAsync(
                firstPage.NextSortOrder,
                2,
                TestContext.Current.CancellationToken);

            Assert.Equal(["C", "A"], firstPage.Items.Select(static item => item.DisplayName));
            Assert.Equal("B", Assert.Single(secondPage.Items).DisplayName);
            Assert.Null(secondPage.NextSortOrder);
            Assert.All(firstPage.Items.Concat(secondPage.Items), item => Assert.True(item.Revision > 1));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static LauncherTargetDescriptor Target(
        string path,
        string name,
        LauncherItemKind kind = LauncherItemKind.File) =>
        new(
            kind,
            path,
            $"{(int)kind}:{path}",
            name,
            "test-fingerprint");

    private static void CreateLegacyDatabase(string databasePath)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE launcher_items
            (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                normalized_path TEXT NOT NULL,
                path_key        TEXT NOT NULL UNIQUE,
                display_name    TEXT NOT NULL,
                kind            INTEGER NOT NULL CHECK (kind IN (1, 2)),
                sort_order      INTEGER NOT NULL UNIQUE,
                created_utc     TEXT NOT NULL
            );
            CREATE INDEX ix_launcher_items_order
                ON launcher_items(sort_order, id);
            CREATE INDEX ix_launcher_items_sort
                ON launcher_items(sort_order, id);
            INSERT INTO launcher_items
                (normalized_path, path_key, display_name, kind, sort_order, created_utc)
            VALUES
                ('C:\Tools\Alpha.exe', 'C:\TOOLS\ALPHA.EXE', 'Alpha', 1, 0, '2026-08-24T18:49:19.2602723+00:00'),
                ('C:\Tools\Beta.lnk', 'C:\TOOLS\BETA.LNK', 'Beta', 2, 1, '2026-08-24T18:49:22.7260737+00:00');
            PRAGMA user_version = 1;
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

    private static IReadOnlyList<string> ReadColumnNames(
        SqliteConnection connection,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static long ExecuteInt64(
        SqliteConnection connection,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ExecuteString(
        SqliteConnection connection,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar())!;
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
