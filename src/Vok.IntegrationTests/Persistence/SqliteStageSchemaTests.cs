using System.IO;
using Microsoft.Data.Sqlite;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Persistence;

public sealed class SqliteStageSchemaTests
{
    [Fact]
    public async Task SchemaSixAddsCurrentStageSchemaTransactionallyAndRemainsIdempotent()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "schema6.db");
        try
        {
            await InitializeAsync(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DROP TABLE stage_items;
                    CREATE TABLE preserved_data (
                        id INTEGER PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    INSERT INTO preserved_data (id, value) VALUES (1, 'keep-me');
                    INSERT INTO launcher_items (
                        id, target_kind, target_value, canonical_key,
                        resolved_name, custom_name, sort_order, revision,
                        created_utc, updated_utc)
                    VALUES (
                        '00000000-0000-0000-0000-000000000001', 0,
                        'C:\Launcher.exe', '0:C:\Launcher.exe',
                        'Launcher kept', NULL, 0, 1,
                        '2026-09-04T00:00:00.0000000+00:00',
                        '2026-09-04T00:00:00.0000000+00:00');
                    INSERT INTO clipboard_entries (
                        id, kind, semantic_hash, plain_preview,
                        first_captured_utc, last_captured_utc,
                        last_capture_order, capture_count, is_pinned, revision,
                        file_intent, item_count, total_bytes)
                    VALUES (
                        '00000000-0000-0000-0000-000000000002', 1,
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        '[image]',
                        '2026-09-04T00:00:00.0000000+00:00',
                        '2026-09-04T00:00:00.0000000+00:00',
                        1, 1, 0, 1, NULL, 0, 7);
                    INSERT INTO content_assets (
                        hash, relative_path, media_type, byte_length,
                        pixel_width, pixel_height, state, revision, created_utc)
                    VALUES (
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        'aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png',
                        'image/png', 7, 1, 1, 0, 1,
                        '2026-09-04T00:00:00.0000000+00:00');
                    INSERT INTO clipboard_asset_refs (entry_id, role, asset_hash)
                    VALUES (
                        '00000000-0000-0000-0000-000000000002', 0,
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            await InitializeAsync(databasePath);
            await InitializeAsync(databasePath);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(8L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                "keep-me",
                ExecuteString(reopened, "SELECT value FROM preserved_data WHERE id = 1;"));
            Assert.Equal(
                "Launcher kept",
                ExecuteString(reopened, "SELECT resolved_name FROM launcher_items;"));
            Assert.Equal(1L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM clipboard_entries;"));
            Assert.Equal(1L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM content_assets;"));
            Assert.Equal(1L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM clipboard_asset_refs;"));
            Assert.Equal(
                [
                    "id", "target_kind", "target_value", "canonical_key",
                    "resolved_name", "sort_order", "is_pinned", "revision",
                    "created_utc", "updated_utc",
                ],
                ReadColumnNames(reopened, "stage_items"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_stage_items_canonical';"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_stage_items_canonical';"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_stage_items_order';"));
            Assert.Equal("ok", ExecuteString(reopened, "PRAGMA quick_check;"));
            Assert.Equal(
                0L,
                ExecuteInt64(reopened, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaSevenMigratesCanonicalIndexWithoutChangingStageRowsAndRemainsIdempotent()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "schema7.db");
        try
        {
            CreateSchemaSevenStageDatabase(databasePath);
            IReadOnlyList<StageRowSnapshot> before;
            using (var connection = OpenDatabase(databasePath))
            {
                before = ReadStageRows(connection);
            }

            await InitializeAsync(databasePath);

            string schemaAfterFirstInitialization;
            using (var migrated = OpenDatabase(databasePath))
            {
                Assert.Equal(8L, ExecuteInt64(migrated, "PRAGMA user_version;"));
                Assert.Equal(before, ReadStageRows(migrated));
                Assert.Equal(
                    "keep-me",
                    ExecuteString(migrated, "SELECT value FROM preserved_data WHERE id = 1;"));
                Assert.Equal(
                    0L,
                    ExecuteInt64(
                        migrated,
                        "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                        "WHERE name = 'ux_stage_items_canonical';"));
                Assert.Equal(
                    1L,
                    ExecuteInt64(
                        migrated,
                        "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                        "WHERE name = 'ix_stage_items_canonical' AND [unique] = 0 AND partial = 0;"));
                Assert.Equal(
                    1L,
                    ExecuteInt64(
                        migrated,
                        "SELECT COUNT(*) FROM pragma_index_xinfo('ix_stage_items_canonical') " +
                        "WHERE key = 1 AND name = 'canonical_key' AND coll = 'NOCASE';"));
                Assert.Equal(
                    1L,
                    ExecuteInt64(
                        migrated,
                        "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                        "WHERE name = 'ix_stage_items_order' AND [unique] = 0 AND partial = 0;"));
                Assert.Equal("ok", ExecuteString(migrated, "PRAGMA quick_check;"));
                Assert.Equal(
                    0L,
                    ExecuteInt64(migrated, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
                schemaAfterFirstInitialization = ReadSchemaDefinition(migrated);
            }

            await InitializeAsync(databasePath);

            using (var reopened = OpenDatabase(databasePath))
            {
                Assert.Equal(8L, ExecuteInt64(reopened, "PRAGMA user_version;"));
                Assert.Equal(before, ReadStageRows(reopened));
                Assert.Equal(schemaAfterFirstInitialization, ReadSchemaDefinition(reopened));

                using var insert = reopened.CreateCommand();
                insert.CommandText = """
                    INSERT INTO stage_items VALUES (
                        '00000000-0000-0000-0000-000000000003', 0,
                        'c:\keep-a.txt', 'c:\keep-a.txt', 'duplicate canonical',
                        10, 0, 1,
                        '2026-09-14T00:00:00.0000000+00:00',
                        '2026-09-14T00:00:00.0000000+00:00');
                    """;
                Assert.Equal(1, insert.ExecuteNonQuery());
                Assert.Equal(3L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM stage_items;"));
            }
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task StageSchemaVersionMissingStageTableIsRejectedWithoutSelfHealing(
        int schemaVersion)
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "missing-current-stage.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TABLE preserved_data (
                        id INTEGER PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    INSERT INTO preserved_data (id, value) VALUES (1, 'keep-me');
                    PRAGMA user_version = {schemaVersion};
                    """;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("stage_items table is missing", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(schemaVersion, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                "keep-me",
                ExecuteString(reopened, "SELECT value FROM preserved_data WHERE id = 1;"));
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

    [Theory]
    [InlineData(
        "CREATE INDEX ux_stage_items_canonical ON stage_items(canonical_key); " +
        "CREATE INDEX ix_stage_items_order ON stage_items(sort_order, id);")]
    [InlineData(
        "CREATE UNIQUE INDEX ux_stage_items_canonical " +
        "ON stage_items(canonical_key COLLATE BINARY); " +
        "CREATE INDEX ix_stage_items_order ON stage_items(sort_order, id);")]
    [InlineData(
        "CREATE UNIQUE INDEX ux_stage_items_canonical " +
        "ON stage_items(canonical_key COLLATE NOCASE); " +
        "CREATE INDEX ix_stage_items_order ON stage_items(id, sort_order);")]
    public async Task ExistingStageTableWithInvalidIndexesIsRejectedWithoutRepair(
        string indexSql)
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "invalid-index.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE stage_items (
                        id TEXT NOT NULL PRIMARY KEY,
                        target_kind INTEGER NOT NULL,
                        target_value TEXT NOT NULL,
                        canonical_key TEXT NOT NULL COLLATE NOCASE,
                        resolved_name TEXT NOT NULL,
                        sort_order INTEGER NOT NULL,
                        is_pinned INTEGER NOT NULL,
                        revision INTEGER NOT NULL,
                        created_utc TEXT NOT NULL,
                        updated_utc TEXT NOT NULL
                    );
                    INSERT INTO stage_items VALUES (
                        '00000000-0000-0000-0000-000000000001', 0,
                        'C:\keep.txt', 'C:\KEEP.TXT', 'keep',
                        1, 0, 1,
                        '2026-09-04T00:00:00.0000000+00:00',
                        '2026-09-04T00:00:00.0000000+00:00');
                    PRAGMA user_version = 7;
                    """ + indexSql;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("unsupported indexes", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                "keep",
                ExecuteString(reopened, "SELECT resolved_name FROM stage_items;"));
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
    public async Task FutureSchemaVersionIsRejectedWithoutMutation()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "future.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE preserved_data (value TEXT NOT NULL);
                    INSERT INTO preserved_data (value) VALUES ('future-data');
                    PRAGMA user_version = 9;
                    """;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("newer than the supported version", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(9L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                "future-data",
                ExecuteString(reopened, "SELECT value FROM preserved_data;"));
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
    public async Task StageTableBeforeItsIntroducedVersionIsRejectedWithoutMutation()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "premature-stage.db");
        try
        {
            CreateSchemaSevenStageDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version = 6;";
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("before its introduced schema version", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(6L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(2L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM stage_items;"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                    "WHERE name = 'ux_stage_items_canonical' AND [unique] = 1;"));
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
    public async Task CurrentSchemaWithAnotherCanonicalUniqueConstraintIsRejectedWithoutRepair()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "current-hidden-unique.db");
        try
        {
            CreateSchemaSevenStageDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DROP INDEX ux_stage_items_canonical;
                    CREATE INDEX ix_stage_items_canonical
                    ON stage_items(canonical_key COLLATE NOCASE);
                    CREATE UNIQUE INDEX ux_stage_items_canonical_shadow
                    ON stage_items(canonical_key COLLATE BINARY);
                    PRAGMA user_version = 8;
                    """;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("unsupported indexes", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(8L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(2L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM stage_items;"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                    "WHERE name = 'ux_stage_items_canonical_shadow' AND [unique] = 1;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CurrentSchemaWithLegacyCanonicalIndexNameIsRejectedWithoutRepair()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "current-legacy-index-name.db");
        try
        {
            CreateSchemaSevenStageDatabase(databasePath);
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DROP INDEX ux_stage_items_canonical;
                    CREATE INDEX ix_stage_items_canonical
                    ON stage_items(canonical_key COLLATE NOCASE);
                    CREATE INDEX ux_stage_items_canonical
                    ON stage_items(canonical_key COLLATE NOCASE);
                    PRAGMA user_version = 8;
                    """;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("unsupported indexes", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(8L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(2L, ExecuteInt64(reopened, "SELECT COUNT(*) FROM stage_items;"));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                    "WHERE name = 'ux_stage_items_canonical' AND [unique] = 0;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task SchemaSevenCanonicalIndexNameConflictRollsBackMigration()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "schema7-index-conflict.db");
        try
        {
            CreateSchemaSevenStageDatabase(databasePath);
            IReadOnlyList<StageRowSnapshot> before;
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                before = ReadStageRows(connection);
                command.CommandText = """
                    CREATE TABLE ix_stage_items_canonical (
                        sentinel TEXT NOT NULL
                    );
                    INSERT INTO ix_stage_items_canonical (sentinel) VALUES ('keep-me');
                    """;
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<SqliteException>(() => InitializeAsync(databasePath));

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(7L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(before, ReadStageRows(reopened));
            Assert.Equal(
                1L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                    "WHERE name = 'ux_stage_items_canonical' AND [unique] = 1;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM pragma_index_list('stage_items') " +
                    "WHERE name = 'ix_stage_items_canonical';"));
            Assert.Equal(
                "keep-me",
                ExecuteString(reopened, "SELECT sentinel FROM ix_stage_items_canonical;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task UnsupportedStageSchemaFailsWithoutReplacingUserData()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "unsupported-stage.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE stage_items (
                        id TEXT PRIMARY KEY,
                        sentinel TEXT NOT NULL
                    );
                    INSERT INTO stage_items (id, sentinel) VALUES ('keep', 'user-data');
                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InitializeAsync(databasePath));
            Assert.Contains("unsupported schema", exception.Message);

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(6L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                "user-data",
                ExecuteString(reopened, "SELECT sentinel FROM stage_items WHERE id = 'keep';"));
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
    public async Task IndexNameConflictRollsBackStageTableAndVersionChange()
    {
        var directory = CreateTestDirectory();
        var databasePath = Path.Combine(directory, "index-conflict.db");
        try
        {
            using (var connection = OpenDatabase(databasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE ix_stage_items_order (
                        sentinel TEXT NOT NULL
                    );
                    INSERT INTO ix_stage_items_order (sentinel) VALUES ('keep-me');
                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<SqliteException>(() => InitializeAsync(databasePath));

            using var reopened = OpenDatabase(databasePath);
            Assert.Equal(6L, ExecuteInt64(reopened, "PRAGMA user_version;"));
            Assert.Equal(
                0L,
                ExecuteInt64(
                    reopened,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'stage_items';"));
            Assert.Equal(
                "keep-me",
                ExecuteString(reopened, "SELECT sentinel FROM ix_stage_items_order;"));
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    private static async Task InitializeAsync(string databasePath)
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

    private static string ReadSchemaDefinition(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, coalesce(sql, '')
            FROM sqlite_schema
            ORDER BY type, name;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join('|',
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return string.Join('\n', rows);
    }

    private static IReadOnlyList<StageRowSnapshot> ReadStageRows(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<StageRowSnapshot>();
        while (reader.Read())
        {
            rows.Add(new StageRowSnapshot(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }

    private static void CreateSchemaSevenStageDatabase(string databasePath)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE preserved_data (
                id INTEGER PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO preserved_data (id, value) VALUES (1, 'keep-me');

            CREATE TABLE stage_items (
                id TEXT NOT NULL PRIMARY KEY,
                target_kind INTEGER NOT NULL,
                target_value TEXT NOT NULL,
                canonical_key TEXT NOT NULL COLLATE NOCASE,
                resolved_name TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                is_pinned INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_stage_items_canonical
            ON stage_items(canonical_key COLLATE NOCASE);
            CREATE INDEX ix_stage_items_order
            ON stage_items(sort_order, id);

            INSERT INTO stage_items VALUES
                ('00000000-0000-0000-0000-000000000001', 0,
                 'C:\keep-a.txt', 'C:\KEEP-A.TXT', 'keep A',
                 20, 1, 7,
                 '2026-09-12T00:00:00.0000000+00:00',
                 '2026-09-13T00:00:00.0000000+00:00'),
                ('00000000-0000-0000-0000-000000000002', 1,
                 'C:\keep-b', 'C:\KEEP-B', 'keep B',
                 10, 0, 3,
                 '2026-09-10T00:00:00.0000000+00:00',
                 '2026-09-11T00:00:00.0000000+00:00');
            PRAGMA user_version = 7;
            """;
        command.ExecuteNonQuery();
    }

    private sealed record StageRowSnapshot(
        string Id,
        long TargetKind,
        string TargetValue,
        string CanonicalKey,
        string ResolvedName,
        long SortOrder,
        long IsPinned,
        long Revision,
        string CreatedUtc,
        string UpdatedUtc);

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
