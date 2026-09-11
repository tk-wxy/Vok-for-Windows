using System.Globalization;
using Microsoft.Data.Sqlite;
using Vok.Core.Stage;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteStageRepository : IStageRepository, IDisposable
{
    internal const long SortOrderStep = 1L << 20;
    private const int MaximumPageSize = 256;
    private readonly SqliteStorageExecutor _executor;
    private readonly StageOptions _options;
    private readonly bool _ownsExecutor;

    public SqliteStageRepository(
        string databasePath,
        StageOptions? options = null)
        : this(
            new SqliteStorageExecutor(databasePath),
            options ?? new StageOptions(),
            ownsExecutor: true)
    {
    }

    public SqliteStageRepository(
        SqliteStorageExecutor executor,
        StageOptions options)
        : this(executor, options, ownsExecutor: false)
    {
    }

    private SqliteStageRepository(
        SqliteStorageExecutor executor,
        StageOptions options,
        bool ownsExecutor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsExecutor = ownsExecutor;
    }

    public Task<StagePage> LoadPageAsync(
        StagePageCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (after is { Id: var id } && id == Guid.Empty)
        {
            throw new ArgumentException(
                "A Stage page cursor must contain a non-empty item id.",
                nameof(after));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        return _executor.ExecuteAsync(
            connection => LoadPage(connection, after, pageSize),
            cancellationToken);
    }

    public Task<StageAddOrMoveResult> AddOrMoveAsync(
        IReadOnlyList<StageTargetDescriptor> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException(
                "At least one Stage target is required.",
                nameof(targets));
        }

        var snapshot = targets.ToArray();
        foreach (var target in snapshot)
        {
            ValidateTarget(target);
        }

        return _executor.ExecuteAsync(
            connection => AddOrMove(connection, snapshot, _options.Capacity),
            cancellationToken);
    }

    public Task<StageRemoveResult> RemoveAsync(
        Guid itemId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return _executor.ExecuteAsync(
            connection => Remove(connection, itemId, expectedRevision),
            cancellationToken);
    }

    public Task<StageItemMutationResult> SetPinnedAsync(
        Guid itemId,
        long expectedRevision,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return _executor.ExecuteAsync(
            connection => SetPinned(connection, itemId, expectedRevision, isPinned),
            cancellationToken);
    }

    public Task<StageRelinkResult> RelinkAsync(
        Guid itemId,
        long expectedRevision,
        StageTargetDescriptor target,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(itemId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        ValidateTarget(target);
        return _executor.ExecuteAsync(
            connection => Relink(connection, itemId, expectedRevision, target),
            cancellationToken);
    }

    public Task<StageReorderResult> ReorderAsync(
        StageReorderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _executor.ExecuteAsync(
            connection => Reorder(connection, request),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsExecutor)
        {
            _executor.Dispose();
        }
    }

    private static StagePage LoadPage(
        SqliteConnection connection,
        StagePageCursor? after,
        int pageSize)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM stage_items;";
        var totalCount = Convert.ToInt64(
            countCommand.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE $afterOrder IS NULL
               OR sort_order < $afterOrder
               OR (sort_order = $afterOrder AND id < $afterId)
            ORDER BY sort_order DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$afterOrder",
            after is null ? DBNull.Value : after.Value.SortOrder);
        command.Parameters.AddWithValue(
            "$afterId",
            after is null ? DBNull.Value : after.Value.Id.ToString("D"));
        command.Parameters.AddWithValue("$limit", pageSize + 1);

        var items = new List<StageItem>(Math.Min(pageSize + 1, 257));
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }
        }

        var hasMore = items.Count > pageSize;
        StageOrderAnchor? olderBoundary = hasMore
            ? ToOrderAnchor(items[pageSize])
            : null;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        StageOrderAnchor? newerBoundary = items.Count == 0
            ? null
            : ReadImmediateNewerBoundary(connection, transaction, items[0]);
        StagePageCursor? next = hasMore
            ? new StagePageCursor(items[^1].SortOrder, items[^1].Id)
            : null;
        transaction.Commit();
        return new StagePage(
            items.AsReadOnly(),
            next,
            totalCount,
            newerBoundary,
            olderBoundary);
    }

    private static StageOrderAnchor? ReadImmediateNewerBoundary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageItem firstItem)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, revision, sort_order
            FROM stage_items
            WHERE sort_order > $sortOrder
               OR (sort_order = $sortOrder AND id > $id)
            ORDER BY sort_order ASC, id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sortOrder", firstItem.SortOrder);
        command.Parameters.AddWithValue("$id", firstItem.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StageOrderAnchor(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2))
            : null;
    }

    private static StageOrderAnchor ToOrderAnchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);

    private static StageAddOrMoveResult AddOrMove(
        SqliteConnection connection,
        IReadOnlyList<StageTargetDescriptor> targets,
        long capacity)
    {
        var uniqueTargets = new List<StageTargetDescriptor>(targets.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (seen.Add(target.CanonicalKey))
            {
                uniqueTargets.Add(target);
            }
        }

        using var transaction = connection.BeginTransaction();
        var countBefore = ReadCount(connection, transaction);
        var existing = FindExisting(connection, transaction, uniqueTargets);
        var addedCount = uniqueTargets.Count - existing.Count;
        if (addedCount > 0 &&
            (countBefore >= capacity || addedCount > capacity - countBefore))
        {
            transaction.Rollback();
            return new StageAddOrMoveResult(
                StageAddOrMoveStatus.CapacityExceeded,
                countBefore,
                countBefore,
                0,
                0,
                capacity);
        }

        var maximumSortOrder = ReadMaximumSortOrder(connection, transaction);
        long highestSortOrder;
        try
        {
            highestSortOrder = checked(
                maximumSortOrder + checked(uniqueTargets.Count * SortOrderStep));
        }
        catch (OverflowException)
        {
            transaction.Rollback();
            return new StageAddOrMoveResult(
                StageAddOrMoveStatus.SortOrderExhausted,
                countBefore,
                countBefore,
                0,
                0,
                capacity);
        }

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < uniqueTargets.Count; index++)
        {
            var target = uniqueTargets[index];
            var sortOrder = highestSortOrder - (index * SortOrderStep);
            if (existing.TryGetValue(target.CanonicalKey, out var item))
            {
                UpdateExisting(
                    connection,
                    transaction,
                    item,
                    target,
                    sortOrder,
                    now);
            }
            else
            {
                Insert(
                    connection,
                    transaction,
                    target,
                    sortOrder,
                    now);
            }
        }

        transaction.Commit();
        return new StageAddOrMoveResult(
            StageAddOrMoveStatus.Applied,
            countBefore,
            checked(countBefore + addedCount),
            addedCount,
            existing.Count,
            capacity);
    }

    private static StageRemoveResult Remove(
        SqliteConnection connection,
        Guid itemId,
        long expectedRevision)
    {
        using var transaction = connection.BeginTransaction();
        long? actualRevision;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT revision FROM stage_items WHERE id = $id;";
            read.Parameters.AddWithValue("$id", itemId.ToString("D"));
            var value = read.ExecuteScalar();
            actualRevision = value is null or DBNull
                ? null
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        if (actualRevision is null)
        {
            transaction.Rollback();
            return new StageRemoveResult(StageRemoveStatus.NotFound);
        }

        if (actualRevision.Value != expectedRevision)
        {
            transaction.Rollback();
            return new StageRemoveResult(StageRemoveStatus.RevisionConflict);
        }

        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM stage_items
                WHERE id = $id AND revision = $revision;
                """;
            remove.Parameters.AddWithValue("$id", itemId.ToString("D"));
            remove.Parameters.AddWithValue("$revision", expectedRevision);
            if (remove.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Stage item {itemId:D} changed during removal.");
            }
        }

        transaction.Commit();
        return new StageRemoveResult(StageRemoveStatus.Removed);
    }

    private static StageItemMutationResult SetPinned(
        SqliteConnection connection,
        Guid itemId,
        long expectedRevision,
        bool isPinned)
    {
        using var transaction = connection.BeginTransaction();
        var item = ReadItemById(connection, transaction, itemId);
        if (item is null)
        {
            transaction.Rollback();
            return new StageItemMutationResult(
                StageItemMutationStatus.NotFound,
                null);
        }

        if (item.Revision != expectedRevision)
        {
            transaction.Rollback();
            return new StageItemMutationResult(
                StageItemMutationStatus.RevisionConflict,
                null);
        }

        if (item.IsPinned == isPinned)
        {
            transaction.Rollback();
            return new StageItemMutationResult(
                StageItemMutationStatus.NoChange,
                item);
        }

        var nextRevision = checked(item.Revision + 1);
        var updatedUtc = DateTimeOffset.UtcNow;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE stage_items
                SET is_pinned = $isPinned,
                    revision = $nextRevision,
                    updated_utc = $updatedUtc
                WHERE id = $id AND revision = $expectedRevision;
                """;
            update.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
            update.Parameters.AddWithValue("$nextRevision", nextRevision);
            update.Parameters.AddWithValue("$updatedUtc", FormatUtc(updatedUtc));
            update.Parameters.AddWithValue("$id", itemId.ToString("D"));
            update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Stage item {itemId:D} changed while updating its pinned state.");
            }
        }

        var updated = ReadItemById(connection, transaction, itemId) ??
            throw new InvalidOperationException(
                $"Stage item {itemId:D} disappeared after updating its pinned state.");
        transaction.Commit();
        return new StageItemMutationResult(
            StageItemMutationStatus.Applied,
            updated);
    }

    private static StageRelinkResult Relink(
        SqliteConnection connection,
        Guid itemId,
        long expectedRevision,
        StageTargetDescriptor target)
    {
        using var transaction = connection.BeginTransaction();
        var current = ReadItemById(connection, transaction, itemId);
        if (current is null)
        {
            transaction.Rollback();
            return new StageRelinkResult(StageRelinkStatus.NotFound, null);
        }

        if (current.Revision != expectedRevision)
        {
            transaction.Rollback();
            return new StageRelinkResult(StageRelinkStatus.RevisionConflict, null);
        }

        if (current.Kind != target.Kind)
        {
            transaction.Rollback();
            return new StageRelinkResult(StageRelinkStatus.KindMismatch, null);
        }

        if (string.Equals(
                current.CanonicalKey,
                target.CanonicalKey,
                StringComparison.OrdinalIgnoreCase))
        {
            transaction.Rollback();
            return new StageRelinkResult(StageRelinkStatus.NoChange, current);
        }

        using (var conflict = connection.CreateCommand())
        {
            conflict.Transaction = transaction;
            conflict.CommandText = """
                SELECT 1
                FROM stage_items
                WHERE canonical_key = $key COLLATE NOCASE
                  AND id <> $id
                LIMIT 1;
                """;
            conflict.Parameters.AddWithValue("$key", target.CanonicalKey);
            conflict.Parameters.AddWithValue("$id", itemId.ToString("D"));
            if (conflict.ExecuteScalar() is not null)
            {
                transaction.Rollback();
                return new StageRelinkResult(
                    StageRelinkStatus.CanonicalConflict,
                    null);
            }
        }

        var nextRevision = checked(current.Revision + 1);
        var updatedUtc = DateTimeOffset.UtcNow;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE stage_items
                SET target_kind = $kind,
                    target_value = $target,
                    canonical_key = $key,
                    resolved_name = $name,
                    revision = $nextRevision,
                    updated_utc = $updatedUtc
                WHERE id = $id AND revision = $expectedRevision;
                """;
            update.Parameters.AddWithValue("$kind", (int)target.Kind);
            update.Parameters.AddWithValue("$target", target.TargetValue);
            update.Parameters.AddWithValue("$key", target.CanonicalKey);
            update.Parameters.AddWithValue("$name", target.ResolvedName);
            update.Parameters.AddWithValue("$nextRevision", nextRevision);
            update.Parameters.AddWithValue("$updatedUtc", FormatUtc(updatedUtc));
            update.Parameters.AddWithValue("$id", itemId.ToString("D"));
            update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Stage item {itemId:D} changed while relinking its target.");
            }
        }

        var updated = ReadItemById(connection, transaction, itemId) ??
            throw new InvalidOperationException(
                $"Stage item {itemId:D} disappeared after relinking its target.");
        transaction.Commit();
        return new StageRelinkResult(StageRelinkStatus.Applied, updated);
    }

    private static StageReorderResult Reorder(
        SqliteConnection connection,
        StageReorderRequest request)
    {
        using var transaction = connection.BeginTransaction();
        var source = ReadItemById(connection, transaction, request.Source.Id);
        if (source is null)
        {
            return RejectReorder(transaction, StageReorderStatus.NotFound);
        }

        if (source.Revision != request.Source.Revision)
        {
            return RejectReorder(transaction, StageReorderStatus.RevisionConflict);
        }

        var anchorStatus = ValidateStoredAnchor(
            connection,
            transaction,
            request.NewerNeighbor);
        if (anchorStatus is not null)
        {
            return RejectReorder(transaction, anchorStatus.Value);
        }

        anchorStatus = ValidateStoredAnchor(
            connection,
            transaction,
            request.OlderNeighbor);
        if (anchorStatus is not null)
        {
            return RejectReorder(transaction, anchorStatus.Value);
        }

        var actualOlderAtTarget = ReadOlderAfterTargetAnchor(
            connection,
            transaction,
            source.Id,
            request.NewerNeighbor);
        if (!HasIdentity(actualOlderAtTarget, request.OlderNeighbor))
        {
            return RejectReorder(transaction, StageReorderStatus.NeighborConflict);
        }

        var currentNewer = ReadImmediateNewer(connection, transaction, source);
        var currentOlder = ReadImmediateOlder(connection, transaction, source);
        if (HasIdentity(currentNewer, request.NewerNeighbor) &&
            HasIdentity(currentOlder, request.OlderNeighbor))
        {
            return RejectReorder(transaction, StageReorderStatus.NoChange);
        }

        IReadOnlyList<StageOrderRebalanceAssignment> assignments;
        if (TryCalculateSortOrder(
                source,
                request.NewerNeighbor,
                request.OlderNeighbor,
                out var sortOrder))
        {
            assignments = [new StageOrderRebalanceAssignment(source, sortOrder)];
        }
        else
        {
            var newerCandidates = ReadRebalanceNewerCandidates(
                connection,
                transaction,
                source.Id,
                request.NewerNeighbor);
            var olderCandidates = ReadRebalanceOlderCandidates(
                connection,
                transaction,
                source.Id,
                request.OlderNeighbor);
            EnsureCandidateStartsAtAnchor(
                newerCandidates,
                request.NewerNeighbor,
                "newer");
            EnsureCandidateStartsAtAnchor(
                olderCandidates,
                request.OlderNeighbor,
                "older");
            var plan = StageOrderRebalancePlanner.TryCreate(
                source,
                newerCandidates,
                olderCandidates,
                SortOrderStep);
            if (plan is null)
            {
                return RejectReorder(
                    transaction,
                    StageReorderStatus.SortOrderExhausted);
            }

            assignments = plan.Assignments;
        }

        var writes = assignments
            .Where(static assignment =>
                assignment.Item.SortOrder != assignment.SortOrder)
            .Select(static assignment => new StageReorderWrite(
                assignment.Item,
                assignment.SortOrder,
                checked(assignment.Item.Revision + 1)))
            .ToArray();
        if (writes.Length is 0 or > StageReorderResult.MaximumChangedItems)
        {
            throw new InvalidOperationException(
                "A Stage reorder produced an invalid bounded write plan.");
        }

        if (writes.Select(static write => write.Item.Id).Distinct().Count() !=
            writes.Length)
        {
            throw new InvalidOperationException(
                "A Stage reorder produced duplicate bounded write entries.");
        }

        var updatedUtc = DateTimeOffset.UtcNow;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE stage_items
            SET sort_order = $sortOrder,
                revision = $nextRevision,
                updated_utc = $updatedUtc
            WHERE id = $id AND revision = $expectedRevision;
            """;
        var sortOrderParameter = update.Parameters.Add(
            "$sortOrder",
            SqliteType.Integer);
        var nextRevisionParameter = update.Parameters.Add(
            "$nextRevision",
            SqliteType.Integer);
        update.Parameters.AddWithValue("$updatedUtc", FormatUtc(updatedUtc));
        var idParameter = update.Parameters.Add("$id", SqliteType.Text);
        var expectedRevisionParameter = update.Parameters.Add(
            "$expectedRevision",
            SqliteType.Integer);
        foreach (var write in writes)
        {
            sortOrderParameter.Value = write.SortOrder;
            nextRevisionParameter.Value = write.NextRevision;
            idParameter.Value = write.Item.Id.ToString("D");
            expectedRevisionParameter.Value = write.Item.Revision;
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Stage item {write.Item.Id:D} changed while reordering.");
            }
        }

        var changedItems = new List<StageItem>(writes.Length);
        foreach (var write in writes)
        {
            var updated = ReadItemById(connection, transaction, write.Item.Id) ??
                throw new InvalidOperationException(
                    $"Stage item {write.Item.Id:D} disappeared after reordering.");
            if (updated.SortOrder != write.SortOrder ||
                updated.Revision != write.NextRevision)
            {
                throw new InvalidOperationException(
                    $"Stage item {write.Item.Id:D} did not retain its planned order update.");
            }

            changedItems.Add(updated);
        }

        var result = new StageReorderResult(
            StageReorderStatus.Applied,
            changedItems);
        transaction.Commit();
        return result;
    }

    private static StageReorderStatus? ValidateStoredAnchor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageOrderAnchor? anchor)
    {
        if (anchor is not { } expected)
        {
            return null;
        }

        var stored = ReadItemById(connection, transaction, expected.Id);
        if (stored is null)
        {
            return StageReorderStatus.NeighborConflict;
        }

        if (stored.Revision != expected.Revision)
        {
            return StageReorderStatus.RevisionConflict;
        }

        return stored.SortOrder == expected.SortOrder
            ? null
            : StageReorderStatus.NeighborConflict;
    }

    private static StageItem? ReadOlderAfterTargetAnchor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceId,
        StageOrderAnchor? newerNeighbor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = newerNeighbor is null
            ? """
                SELECT id, target_kind, target_value, canonical_key,
                       resolved_name, sort_order, is_pinned, revision,
                       created_utc, updated_utc
                FROM stage_items
                WHERE id <> $sourceId
                ORDER BY sort_order DESC, id DESC
                LIMIT 1;
                """
            : """
                SELECT id, target_kind, target_value, canonical_key,
                       resolved_name, sort_order, is_pinned, revision,
                       created_utc, updated_utc
                FROM stage_items
                WHERE id <> $sourceId
                  AND (sort_order < $newerSortOrder
                       OR (sort_order = $newerSortOrder AND id < $newerId))
                ORDER BY sort_order DESC, id DESC
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        if (newerNeighbor is { } newer)
        {
            command.Parameters.AddWithValue("$newerSortOrder", newer.SortOrder);
            command.Parameters.AddWithValue("$newerId", newer.Id.ToString("D"));
        }

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static StageItem? ReadImmediateNewer(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageItem source)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE sort_order > $sortOrder
               OR (sort_order = $sortOrder AND id > $id)
            ORDER BY sort_order ASC, id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sortOrder", source.SortOrder);
        command.Parameters.AddWithValue("$id", source.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static StageItem? ReadImmediateOlder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageItem source)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE sort_order < $sortOrder
               OR (sort_order = $sortOrder AND id < $id)
            ORDER BY sort_order DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sortOrder", source.SortOrder);
        command.Parameters.AddWithValue("$id", source.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static IReadOnlyList<StageItem> ReadRebalanceNewerCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceId,
        StageOrderAnchor? newerNeighbor)
    {
        if (newerNeighbor is not { } anchor)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE id <> $sourceId
              AND (sort_order > $sortOrder
                   OR (sort_order = $sortOrder AND id >= $id))
            ORDER BY sort_order ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        command.Parameters.AddWithValue("$sortOrder", anchor.SortOrder);
        command.Parameters.AddWithValue("$id", anchor.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$limit",
            StageReorderResult.MaximumChangedItems);
        return ReadItems(command);
    }

    private static IReadOnlyList<StageItem> ReadRebalanceOlderCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceId,
        StageOrderAnchor? olderNeighbor)
    {
        if (olderNeighbor is not { } anchor)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE id <> $sourceId
              AND (sort_order < $sortOrder
                   OR (sort_order = $sortOrder AND id <= $id))
            ORDER BY sort_order DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString("D"));
        command.Parameters.AddWithValue("$sortOrder", anchor.SortOrder);
        command.Parameters.AddWithValue("$id", anchor.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$limit",
            StageReorderResult.MaximumChangedItems);
        return ReadItems(command);
    }

    private static IReadOnlyList<StageItem> ReadItems(SqliteCommand command)
    {
        var items = new List<StageItem>(StageReorderResult.MaximumChangedItems);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static void EnsureCandidateStartsAtAnchor(
        IReadOnlyList<StageItem> candidates,
        StageOrderAnchor? anchor,
        string direction)
    {
        if (anchor is null)
        {
            if (candidates.Count != 0)
            {
                throw new InvalidOperationException(
                    $"A boundary-free Stage {direction} candidate read returned rows.");
            }

            return;
        }

        if (candidates.Count == 0 || candidates[0].Id != anchor.Value.Id)
        {
            throw new InvalidOperationException(
                $"The Stage {direction} rebalance candidates did not begin at the target anchor.");
        }
    }

    private static bool TryCalculateSortOrder(
        StageItem source,
        StageOrderAnchor? newerNeighbor,
        StageOrderAnchor? olderNeighbor,
        out long sortOrder)
    {
        Int128 candidate;
        if (newerNeighbor is { } newer && olderNeighbor is { } older)
        {
            var gap = (Int128)newer.SortOrder - older.SortOrder;
            if (gap < 2)
            {
                sortOrder = default;
                return false;
            }

            candidate = (Int128)older.SortOrder + (gap / 2);
        }
        else if (olderNeighbor is { } first)
        {
            candidate = (Int128)first.SortOrder + SortOrderStep;
        }
        else if (newerNeighbor is { } last)
        {
            candidate = (Int128)last.SortOrder - SortOrderStep;
        }
        else
        {
            sortOrder = default;
            return false;
        }

        if (candidate < long.MinValue || candidate > long.MaxValue)
        {
            sortOrder = default;
            return false;
        }

        sortOrder = (long)candidate;
        var candidateAnchor = new StageOrderAnchor(
            source.Id,
            source.Revision,
            sortOrder);
        return (newerNeighbor is null ||
                candidateAnchor.CompareTo(newerNeighbor.Value) < 0) &&
               (olderNeighbor is null ||
                candidateAnchor.CompareTo(olderNeighbor.Value) > 0);
    }

    private static bool HasIdentity(
        StageItem? item,
        StageOrderAnchor? anchor) =>
        item?.Id == anchor?.Id;

    private static StageReorderResult RejectReorder(
        SqliteTransaction transaction,
        StageReorderStatus status)
    {
        transaction.Rollback();
        return new StageReorderResult(status, []);
    }

    private readonly record struct StageReorderWrite(
        StageItem Item,
        long SortOrder,
        long NextRevision);

    private static StageItem? ReadItemById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static long ReadCount(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM stage_items;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ReadMaximumSortOrder(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(sort_order) FROM stage_items;";
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, StageItem> FindExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<StageTargetDescriptor> targets)
    {
        var result = new Dictionary<string, StageItem>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, target_kind, target_value, canonical_key,
                   resolved_name, sort_order, is_pinned, revision,
                   created_utc, updated_utc
            FROM stage_items
            WHERE canonical_key = $key COLLATE NOCASE
            LIMIT 1;
            """;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        foreach (var target in targets)
        {
            key.Value = target.CanonicalKey;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result.Add(target.CanonicalKey, ReadItem(reader));
            }
        }

        return result;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageTargetDescriptor target,
        long sortOrder,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO stage_items (
                id, target_kind, target_value, canonical_key,
                resolved_name, sort_order, is_pinned, revision,
                created_utc, updated_utc)
            VALUES (
                $id, $kind, $target, $key,
                $name, $sortOrder, 0, 1,
                $createdUtc, $updatedUtc);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)target.Kind);
        command.Parameters.AddWithValue("$target", target.TargetValue);
        command.Parameters.AddWithValue("$key", target.CanonicalKey);
        command.Parameters.AddWithValue("$name", target.ResolvedName);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$createdUtc", FormatUtc(now));
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(now));
        command.ExecuteNonQuery();
    }

    private static void UpdateExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageItem item,
        StageTargetDescriptor target,
        long sortOrder,
        DateTimeOffset now)
    {
        var nextRevision = checked(item.Revision + 1);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE stage_items
            SET target_kind = $kind,
                target_value = $target,
                canonical_key = $key,
                resolved_name = $name,
                sort_order = $sortOrder,
                revision = $revision,
                updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$kind", (int)target.Kind);
        command.Parameters.AddWithValue("$target", target.TargetValue);
        command.Parameters.AddWithValue("$key", target.CanonicalKey);
        command.Parameters.AddWithValue("$name", target.ResolvedName);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(now));
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Stage item {item.Id:D} disappeared during AddOrMove.");
        }
    }

    private static StageItem ReadItem(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        (StageItemKind)reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetInt64(6) != 0,
        reader.GetInt64(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateTarget(StageTargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.TargetValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.CanonicalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ResolvedName);
        if (!Enum.IsDefined(target.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target.Kind, null);
        }
    }
}
