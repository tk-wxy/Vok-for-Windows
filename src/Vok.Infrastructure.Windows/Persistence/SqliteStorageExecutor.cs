using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteStorageExecutor : IDisposable
{
    private const int QueueCapacity = 64;
    private const int CurrentSchemaVersion = 2;
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
