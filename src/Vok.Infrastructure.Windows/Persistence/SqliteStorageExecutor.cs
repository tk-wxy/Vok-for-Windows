using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteStorageExecutor : IDisposable
{
    private const int QueueCapacity = 64;
    private const int CurrentSchemaVersion = 7;
    private static readonly HashSet<string> CurrentLauncherColumns =
    [
        "id",
        "target_kind",
        "target_value",
        "canonical_key",
        "resolved_name",
        "custom_name",
        "sort_order",
        "revision",
        "created_utc",
        "updated_utc",
    ];
    private static readonly HashSet<string> LegacyLauncherColumns =
    [
        "id",
        "normalized_path",
        "path_key",
        "display_name",
        "kind",
        "sort_order",
        "created_utc",
    ];
    private static readonly HashSet<string> LegacyClipboardEntryColumns =
    [
        "id",
        "kind",
        "semantic_hash",
        "plain_preview",
        "first_captured_utc",
        "last_captured_utc",
        "last_capture_order",
        "capture_count",
        "is_pinned",
        "revision",
    ];
    private static readonly HashSet<string> SchemaFourClipboardEntryColumns =
    [
        .. LegacyClipboardEntryColumns,
        "file_intent",
        "item_count",
    ];
    private static readonly HashSet<string> SchemaFiveClipboardEntryColumns =
    [
        .. SchemaFourClipboardEntryColumns,
        "pixel_width",
        "pixel_height",
        "has_alpha",
        "dpi_x_milli",
        "dpi_y_milli",
    ];
    private static readonly HashSet<string> CurrentClipboardEntryColumns =
    [
        .. SchemaFiveClipboardEntryColumns,
        "total_bytes",
    ];
    private static readonly HashSet<string> CurrentClipboardRepresentationColumns =
    [
        "entry_id",
        "representation_kind",
        "fidelity_rank",
        "inline_text",
        "byte_length",
    ];
    private static readonly HashSet<string> CurrentClipboardFileColumns =
    [
        "entry_id",
        "ordinal",
        "original_path",
        "canonical_path",
        "display_name",
    ];
    private static readonly HashSet<string> CurrentContentAssetColumns =
    [
        "hash",
        "relative_path",
        "media_type",
        "byte_length",
        "pixel_width",
        "pixel_height",
        "state",
        "revision",
        "created_utc",
    ];
    private static readonly HashSet<string> CurrentClipboardAssetReferenceColumns =
    [
        "entry_id",
        "role",
        "asset_hash",
    ];
    private static readonly HashSet<string> CurrentStageColumns =
    [
        "id",
        "target_kind",
        "target_value",
        "canonical_key",
        "resolved_name",
        "sort_order",
        "is_pinned",
        "revision",
        "created_utc",
        "updated_utc",
    ];
    private readonly BlockingCollection<IStorageWorkItem> _queue = new(QueueCapacity);
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly string _connectionString;
    private Exception? _terminalFailure;
    private int _disposed;

    public SqliteStorageExecutor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "The database path must include a parent directory.",
                nameof(databasePath));
        }

        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok SQLite executor",
        };
        _thread.Start();
    }

    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var terminalFailure = Volatile.Read(ref _terminalFailure);
        if (terminalFailure is not null)
        {
            throw new InvalidOperationException(
                "The SQLite executor is no longer available.",
                terminalFailure);
        }

        var item = new StorageWorkItem<T>(operation);
        try
        {
            if (!_queue.TryAdd(item))
            {
                throw new InvalidOperationException(
                    "The SQLite executor queue is full.");
            }
        }
        catch (InvalidOperationException exception) when (_queue.IsAddingCompleted)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(SqliteStorageExecutor));
            }

            terminalFailure = Volatile.Read(ref _terminalFailure);
            if (terminalFailure is not null)
            {
                throw new InvalidOperationException(
                    "The SQLite executor is no longer available.",
                    terminalFailure);
            }

            throw new InvalidOperationException(
                "The SQLite executor queue is not accepting work.",
                exception);
        }

        return await item.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            ConfigureConnection(connection);
            _ready.TrySetResult();

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute(connection);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _terminalFailure, exception);
            _queue.CompleteAdding();
            _ready.TrySetException(exception);
            while (_queue.TryTake(out var item))
            {
                item.Fail(exception);
            }
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
            command.ExecuteNonQuery();
        }

        var schemaVersion = ReadSchemaVersion(connection);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The database schema version {schemaVersion} is newer than the supported version {CurrentSchemaVersion}.");
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
                command.ExecuteNonQuery();
            }

            EnsureLauncherSchema(connection, transaction);
            EnsureClipboardSchema(connection, transaction);
            EnsureStageSchema(connection, transaction, schemaVersion);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void EnsureLauncherSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var columns = ReadTableColumns(connection, transaction, "launcher_items");
        if (columns.Count == 0)
        {
            CreateLauncherTable(connection, transaction);
        }
        else if (columns.SetEquals(LegacyLauncherColumns))
        {
            MigrateLegacyLauncherTable(connection, transaction);
        }
        else if (!columns.SetEquals(CurrentLauncherColumns))
        {
            throw new InvalidOperationException(
                "The launcher_items table has an unsupported schema. " +
                $"Found columns: {string.Join(", ", columns.Order())}.");
        }

        using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = """
            DROP INDEX IF EXISTS ix_launcher_items_order;

            CREATE INDEX IF NOT EXISTS ix_launcher_items_sort
            ON launcher_items(sort_order, id);
            """;
        indexCommand.ExecuteNonQuery();
    }

    private static HashSet<string> ReadTableColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void CreateLauncherTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateLegacyLauncherTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DROP INDEX IF EXISTS ix_launcher_items_order;
                DROP INDEX IF EXISTS ix_launcher_items_sort;
                ALTER TABLE launcher_items RENAME TO launcher_items_legacy_v1;
                """;
            command.ExecuteNonQuery();
        }

        CreateLauncherTable(connection, transaction);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO launcher_items (
                    id, target_kind, target_value, canonical_key,
                    resolved_name, custom_name, sort_order, revision,
                    created_utc, updated_utc)
                SELECT
                    lower(hex(randomblob(16))),
                    CASE kind WHEN 1 THEN 0 WHEN 2 THEN 2 END,
                    normalized_path,
                    CAST(CASE kind WHEN 1 THEN 0 WHEN 2 THEN 2 END AS TEXT)
                        || ':' || normalized_path,
                    display_name,
                    NULL,
                    sort_order,
                    1,
                    created_utc,
                    created_utc
                FROM launcher_items_legacy_v1
                ORDER BY sort_order, id;

                DROP TABLE launcher_items_legacy_v1;
                """;
            command.ExecuteNonQuery();
        }
    }

    private static void EnsureStageSchema(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int schemaVersion)
    {
        var columns = ReadTableColumns(connection, transaction, "stage_items");
        if (columns.Count == 0)
        {
            if (schemaVersion >= CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    "The stage_items table is missing from the current database schema.");
            }

            CreateStageTable(connection, transaction);
            CreateStageIndexes(connection, transaction);
        }
        else if (!columns.SetEquals(CurrentStageColumns))
        {
            throw new InvalidOperationException(
                "The stage_items table has an unsupported schema. " +
                $"Found columns: {string.Join(", ", columns.Order())}.");
        }

        if (!HasIndex(
                connection,
                transaction,
                "stage_items",
                "ux_stage_items_canonical",
                isUnique: true,
                ["canonical_key"]) ||
            !HasIndexCollation(
                connection,
                transaction,
                "ux_stage_items_canonical",
                "canonical_key",
                "NOCASE") ||
            !HasIndex(
                connection,
                transaction,
                "stage_items",
                "ix_stage_items_order",
                isUnique: false,
                ["sort_order", "id"]))
        {
            throw new InvalidOperationException(
                "The stage_items table has unsupported indexes.");
        }
    }

    private static void CreateStageIndexes(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE UNIQUE INDEX ux_stage_items_canonical
            ON stage_items(canonical_key COLLATE NOCASE);

            CREATE INDEX ix_stage_items_order
            ON stage_items(sort_order, id);
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateStageTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureClipboardSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var entryColumns = ReadTableColumns(
            connection,
            transaction,
            "clipboard_entries");
        var representationColumns = ReadTableColumns(
            connection,
            transaction,
            "clipboard_representations");
        var fileColumns = ReadTableColumns(
            connection,
            transaction,
            "clipboard_files");
        var assetColumns = ReadTableColumns(
            connection,
            transaction,
            "content_assets");
        var assetReferenceColumns = ReadTableColumns(
            connection,
            transaction,
            "clipboard_asset_refs");
        if (entryColumns.Count == 0 &&
            representationColumns.Count == 0 &&
            fileColumns.Count == 0 &&
            assetColumns.Count == 0 &&
            assetReferenceColumns.Count == 0)
        {
            CreateClipboardTables(connection, transaction);
        }
        else if (entryColumns.SetEquals(LegacyClipboardEntryColumns) &&
                 representationColumns.SetEquals(CurrentClipboardRepresentationColumns) &&
                 fileColumns.Count == 0 &&
                 assetColumns.Count == 0 &&
                 assetReferenceColumns.Count == 0)
        {
            MigrateClipboardSchema3(connection, transaction);
            MigrateClipboardSchema4(connection, transaction);
            MigrateClipboardSchema5(connection, transaction);
        }
        else if (entryColumns.SetEquals(SchemaFourClipboardEntryColumns) &&
                 representationColumns.SetEquals(CurrentClipboardRepresentationColumns) &&
                 fileColumns.SetEquals(CurrentClipboardFileColumns) &&
                 assetColumns.Count == 0 &&
                 assetReferenceColumns.Count == 0)
        {
            MigrateClipboardSchema4(connection, transaction);
            MigrateClipboardSchema5(connection, transaction);
        }
        else if (entryColumns.SetEquals(SchemaFiveClipboardEntryColumns) &&
                 representationColumns.SetEquals(CurrentClipboardRepresentationColumns) &&
                 fileColumns.SetEquals(CurrentClipboardFileColumns) &&
                 assetColumns.SetEquals(CurrentContentAssetColumns) &&
                 assetReferenceColumns.SetEquals(CurrentClipboardAssetReferenceColumns))
        {
            MigrateClipboardSchema5(connection, transaction);
        }
        else if (!entryColumns.SetEquals(CurrentClipboardEntryColumns) ||
                 !representationColumns.SetEquals(CurrentClipboardRepresentationColumns) ||
                 !fileColumns.SetEquals(CurrentClipboardFileColumns) ||
                 !assetColumns.SetEquals(CurrentContentAssetColumns) ||
                 !assetReferenceColumns.SetEquals(CurrentClipboardAssetReferenceColumns))
        {
            throw new InvalidOperationException(
                "The clipboard history tables have an unsupported schema. " +
                $"Entry columns: {string.Join(", ", entryColumns.Order())}; " +
                $"representation columns: {string.Join(", ", representationColumns.Order())}; " +
                $"file columns: {string.Join(", ", fileColumns.Order())}; " +
                $"asset columns: {string.Join(", ", assetColumns.Order())}; " +
                $"asset reference columns: {string.Join(", ", assetReferenceColumns.Order())}.");
        }

        using var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_clipboard_entries_semantic
            ON clipboard_entries(kind, semantic_hash);

            CREATE INDEX IF NOT EXISTS ix_clipboard_entries_capture_order
            ON clipboard_entries(last_capture_order DESC, id DESC);

            CREATE INDEX IF NOT EXISTS ix_clipboard_asset_refs_hash
            ON clipboard_asset_refs(asset_hash);

            CREATE INDEX IF NOT EXISTS ix_content_assets_state
            ON content_assets(state, hash);
            """;
        indexCommand.ExecuteNonQuery();

        if (!HasCascadeEntryForeignKey(
                connection,
                transaction,
                "clipboard_representations") ||
            !HasCascadeEntryForeignKey(
                connection,
                transaction,
                "clipboard_files") ||
            !HasClipboardAssetReferenceForeignKeys(connection, transaction) ||
            !HasIndex(
                connection,
                transaction,
                "clipboard_entries",
                "ux_clipboard_entries_semantic",
                isUnique: true,
                ["kind", "semantic_hash"]) ||
            !HasIndex(
                connection,
                transaction,
                "clipboard_entries",
                "ix_clipboard_entries_capture_order",
                isUnique: false,
                ["last_capture_order", "id"]) ||
            !HasIndex(
                connection,
                transaction,
                "clipboard_asset_refs",
                "ix_clipboard_asset_refs_hash",
                isUnique: false,
                ["asset_hash"]) ||
            !HasIndex(
                connection,
                transaction,
                "content_assets",
                "ix_content_assets_state",
                isUnique: false,
                ["state", "hash"]))
        {
            throw new InvalidOperationException(
                "The clipboard history tables have unsupported foreign keys or indexes.");
        }
    }

    private static bool HasCascadeEntryForeignKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA foreign_key_list({tableName});";
        using var reader = command.ExecuteReader();
        var count = 0;
        var matches = true;
        while (reader.Read())
        {
            count++;
            matches &=
                string.Equals(
                    reader.GetString(2),
                    "clipboard_entries",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    reader.GetString(3),
                    "entry_id",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    reader.GetString(4),
                    "id",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    reader.GetString(6),
                    "CASCADE",
                    StringComparison.OrdinalIgnoreCase);
        }

        return count == 1 && matches;
    }

    private static bool HasClipboardAssetReferenceForeignKeys(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_list(clipboard_asset_refs);";
        using var reader = command.ExecuteReader();
        var entryMatches = false;
        var assetMatches = false;
        var count = 0;
        while (reader.Read())
        {
            count++;
            var table = reader.GetString(2);
            var from = reader.GetString(3);
            var to = reader.GetString(4);
            var onDelete = reader.GetString(6);
            entryMatches |=
                string.Equals(table, "clipboard_entries", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(from, "entry_id", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(to, "id", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(onDelete, "CASCADE", StringComparison.OrdinalIgnoreCase);
            assetMatches |=
                string.Equals(table, "content_assets", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(from, "asset_hash", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(to, "hash", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(onDelete, "RESTRICT", StringComparison.OrdinalIgnoreCase);
        }

        return count == 2 && entryMatches && assetMatches;
    }

    private static bool HasIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string indexName,
        bool isUnique,
        IReadOnlyList<string> expectedColumns)
    {
        var found = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_list({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(
                        reader.GetString(1),
                        indexName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = reader.GetInt32(2) != 0 == isUnique &&
                    reader.GetInt32(4) == 0;
                break;
            }
        }

        if (!found)
        {
            return false;
        }

        var actualColumns = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_info({indexName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                actualColumns.Add(reader.GetString(2));
            }
        }

        return actualColumns.SequenceEqual(
            expectedColumns,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasIndexCollation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexName,
        string columnName,
        string collationName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA index_xinfo({indexName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(5) == 0 || reader.IsDBNull(2))
            {
                continue;
            }

            return string.Equals(
                    reader.GetString(2),
                    columnName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    reader.GetString(4),
                    collationName,
                    StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void CreateClipboardTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                revision INTEGER NOT NULL,
                file_intent INTEGER NULL,
                item_count INTEGER NOT NULL DEFAULT 0,
                pixel_width INTEGER NULL,
                pixel_height INTEGER NULL,
                has_alpha INTEGER NULL,
                dpi_x_milli INTEGER NULL,
                dpi_y_milli INTEGER NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0
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

            CREATE TABLE clipboard_files (
                entry_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                original_path TEXT NOT NULL,
                canonical_path TEXT NOT NULL,
                display_name TEXT NOT NULL,
                PRIMARY KEY (entry_id, ordinal),
                FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE content_assets (
                hash TEXT NOT NULL PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                media_type TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                pixel_width INTEGER NOT NULL,
                pixel_height INTEGER NOT NULL,
                state INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE clipboard_asset_refs (
                entry_id TEXT NOT NULL,
                role INTEGER NOT NULL,
                asset_hash TEXT NOT NULL,
                PRIMARY KEY (entry_id, role),
                FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (asset_hash) REFERENCES content_assets(hash) ON DELETE RESTRICT
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateClipboardSchema3(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateClipboardSchema4(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE clipboard_entries
            ADD COLUMN pixel_width INTEGER NULL;

            ALTER TABLE clipboard_entries
            ADD COLUMN pixel_height INTEGER NULL;

            ALTER TABLE clipboard_entries
            ADD COLUMN has_alpha INTEGER NULL;

            ALTER TABLE clipboard_entries
            ADD COLUMN dpi_x_milli INTEGER NULL;

            ALTER TABLE clipboard_entries
            ADD COLUMN dpi_y_milli INTEGER NULL;

            CREATE TABLE content_assets (
                hash TEXT NOT NULL PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                media_type TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                pixel_width INTEGER NOT NULL,
                pixel_height INTEGER NOT NULL,
                state INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE clipboard_asset_refs (
                entry_id TEXT NOT NULL,
                role INTEGER NOT NULL,
                asset_hash TEXT NOT NULL,
                PRIMARY KEY (entry_id, role),
                FOREIGN KEY (entry_id) REFERENCES clipboard_entries(id) ON DELETE CASCADE,
                FOREIGN KEY (asset_hash) REFERENCES content_assets(hash) ON DELETE RESTRICT
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void MigrateClipboardSchema5(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE clipboard_entries
            ADD COLUMN total_bytes INTEGER NOT NULL DEFAULT 0;

            UPDATE clipboard_entries
            SET total_bytes =
                length(CAST(plain_preview AS BLOB)) +
                CASE kind
                    WHEN 0 THEN COALESCE((
                        SELECT SUM(r.byte_length)
                        FROM clipboard_representations AS r
                        WHERE r.entry_id = clipboard_entries.id), 0)
                    WHEN 2 THEN COALESCE((
                        SELECT SUM(
                            length(CAST(f.original_path AS BLOB)) +
                            length(CAST(f.canonical_path AS BLOB)) +
                            length(CAST(f.display_name AS BLOB)))
                        FROM clipboard_files AS f
                        WHERE f.entry_id = clipboard_entries.id), 0)
                    ELSE 0
                END;
            """;
        command.ExecuteNonQuery();
    }

    private interface IStorageWorkItem
    {
        void Execute(SqliteConnection connection);

        void Fail(Exception exception);
    }

    private sealed class StorageWorkItem<T>(Func<SqliteConnection, T> operation)
        : IStorageWorkItem
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Execute(SqliteConnection connection)
        {
            try
            {
                _completion.TrySetResult(operation(connection));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) =>
            _completion.TrySetException(exception);
    }
}
