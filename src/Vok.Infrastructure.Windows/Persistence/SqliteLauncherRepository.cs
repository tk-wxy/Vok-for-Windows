using System.Globalization;
using Microsoft.Data.Sqlite;
using Vok.Core.Launcher;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteLauncherRepository : ILauncherRepository, IDisposable
{
    private const int MaximumPageSize = 512;
    private readonly SqliteStorageExecutor _executor;
    private readonly bool _ownsExecutor;

    public SqliteLauncherRepository(string databasePath)
        : this(new SqliteStorageExecutor(databasePath), ownsExecutor: true)
    {
    }

    public SqliteLauncherRepository(SqliteStorageExecutor executor)
        : this(executor, ownsExecutor: false)
    {
    }

    private SqliteLauncherRepository(
        SqliteStorageExecutor executor,
        bool ownsExecutor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
    }

    public Task<LauncherPage> LoadPageAsync(
        long? afterSortOrder,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        return _executor.ExecuteAsync(
            connection => LoadPage(connection, afterSortOrder, pageSize),
            cancellationToken);
    }

    public Task<LauncherAddResult> AddAsync(
        IReadOnlyList<LauncherTargetDescriptor> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return Task.FromResult(new LauncherAddResult([], []));
        }

        foreach (var target in targets)
        {
            ValidateTarget(target);
        }

        return _executor.ExecuteAsync(
            connection => Add(connection, targets),
            cancellationToken);
    }

    public Task<bool> RemoveAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        return _executor.ExecuteAsync(
            connection => Remove(connection, itemId),
            cancellationToken);
    }

    public Task<bool> MoveToSlotAsync(
        Guid itemId,
        int slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        return _executor.ExecuteAsync(
            connection => MoveToSlot(connection, itemId, slot),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsExecutor)
        {
            _executor.Dispose();
        }
    }

    private static LauncherPage LoadPage(
        SqliteConnection connection,
        long? afterSortOrder,
        int pageSize)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM launcher_items;";
        var totalCount = Convert.ToInt32(
            countCommand.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, custom_name, sort_order, revision,
                   created_utc, updated_utc
            FROM launcher_items
            WHERE $after IS NULL OR sort_order > $after
            ORDER BY sort_order, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$after",
            afterSortOrder is null ? DBNull.Value : afterSortOrder.Value);
        command.Parameters.AddWithValue("$limit", pageSize);

        var items = new List<LauncherItem>(Math.Min(totalCount, pageSize));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        long? next = items.Count == pageSize ? items[^1].SortOrder : null;
        return new LauncherPage(items, next, totalCount);
    }

    private static LauncherAddResult Add(
        SqliteConnection connection,
        IReadOnlyList<LauncherTargetDescriptor> targets)
    {
        using var transaction = connection.BeginTransaction();
        var added = new List<LauncherItem>();
        var existing = new List<LauncherItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var maxCommand = connection.CreateCommand();
        maxCommand.Transaction = transaction;
        maxCommand.CommandText = "SELECT COALESCE(MAX(sort_order), -1) FROM launcher_items;";
        var nextSortOrder = Convert.ToInt64(
            maxCommand.ExecuteScalar(),
            CultureInfo.InvariantCulture) + 1;

        foreach (var target in targets)
        {
            if (!seen.Add(target.CanonicalKey))
            {
                continue;
            }

            var found = FindByCanonicalKey(connection, transaction, target.CanonicalKey);
            if (found is not null)
            {
                existing.Add(found);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var item = new LauncherItem(
                Guid.NewGuid(),
                target.Kind,
                target.TargetValue,
                target.CanonicalKey,
                target.ResolvedName,
                null,
                nextSortOrder++,
                1,
                now,
                now);
            Insert(connection, transaction, item);
            added.Add(item);
        }

        transaction.Commit();
        return new LauncherAddResult(added, existing);
    }

    private static LauncherItem? FindByCanonicalKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, custom_name, sort_order, revision,
                   created_utc, updated_utc
            FROM launcher_items
            WHERE canonical_key = $key COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", canonicalKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LauncherItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO launcher_items (
                id, target_kind, target_value, canonical_key,
                resolved_name, custom_name, sort_order, revision,
                created_utc, updated_utc)
            VALUES (
                $id, $kind, $target, $key,
                $name, $customName, $sortOrder, $revision,
                $createdUtc, $updatedUtc);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$target", item.TargetValue);
        command.Parameters.AddWithValue("$key", item.CanonicalKey);
        command.Parameters.AddWithValue("$name", item.ResolvedName);
        command.Parameters.AddWithValue(
            "$customName",
            item.CustomName is null ? DBNull.Value : item.CustomName);
        command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
        command.Parameters.AddWithValue("$revision", item.Revision);
        command.Parameters.AddWithValue("$createdUtc", FormatUtc(item.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(item.UpdatedUtc));
        command.ExecuteNonQuery();
    }

    private static bool Remove(SqliteConnection connection, Guid itemId)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM launcher_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        var removed = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return removed;
    }

    private static bool MoveToSlot(
        SqliteConnection connection,
        Guid itemId,
        int slot)
    {
        using var transaction = connection.BeginTransaction();
        var ids = new List<Guid>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM launcher_items ORDER BY sort_order, id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var sourceIndex = ids.IndexOf(itemId);
        if (sourceIndex < 0)
        {
            transaction.Rollback();
            return false;
        }

        var boundedSlot = Math.Clamp(slot, 0, ids.Count);
        var targetIndex = sourceIndex < boundedSlot ? boundedSlot - 1 : boundedSlot;
        targetIndex = Math.Clamp(targetIndex, 0, ids.Count - 1);
        if (targetIndex == sourceIndex)
        {
            transaction.Rollback();
            return false;
        }

        ids.RemoveAt(sourceIndex);
        ids.Insert(targetIndex, itemId);
        var now = FormatUtc(DateTimeOffset.UtcNow);
        for (var index = 0; index < ids.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE launcher_items
                SET sort_order = $sortOrder,
                    revision = revision + 1,
                    updated_utc = $updatedUtc
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$sortOrder", index);
            update.Parameters.AddWithValue("$updatedUtc", now);
            update.Parameters.AddWithValue("$id", ids[index].ToString("D"));
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    private static LauncherItem ReadItem(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        (LauncherItemKind)reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt64(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateTarget(LauncherTargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.TargetValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.CanonicalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ResolvedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.IconSourceFingerprint);
        if (!Enum.IsDefined(target.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target.Kind, null);
        }
    }
}
