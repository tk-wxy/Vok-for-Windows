using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Vok.Infrastructure.Windows.Persistence;

internal sealed class SqliteStorageExecutor : IDisposable
{
    private const int QueueCapacity = 64;
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
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
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
