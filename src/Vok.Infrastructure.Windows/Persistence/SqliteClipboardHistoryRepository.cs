using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteClipboardHistoryRepository :
    IClipboardHistoryRepository,
    IClipboardImageMetadataRepository,
    IDisposable
{
    private const int MaximumPageSize = 256;
    private static readonly ClipboardFileNormalizationOptions FileOptions = new();
    private readonly SqliteStorageExecutor _executor;
    private readonly bool _ownsExecutor;

    public SqliteClipboardHistoryRepository(string databasePath)
        : this(new SqliteStorageExecutor(databasePath), ownsExecutor: true)
    {
    }

    public SqliteClipboardHistoryRepository(SqliteStorageExecutor executor)
        : this(executor, ownsExecutor: false)
    {
    }

    private SqliteClipboardHistoryRepository(
        SqliteStorageExecutor executor,
        bool ownsExecutor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
    }

    public Task<ClipboardPage> LoadPageAsync(
        ClipboardPageCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        return _executor.ExecuteAsync(
            connection => LoadPage(connection, after, pageSize),
            cancellationToken);
    }

    public Task<ClipboardEntry> UpsertTextAsync(
        ClipboardTextContent content,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateTextContent(content);
        return _executor.ExecuteAsync(
            connection => UpsertText(connection, content, capturedUtc),
            cancellationToken);
    }

    public Task<ClipboardEntry> UpsertFileListAsync(
        ClipboardFileListContent content,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateFileListContent(content);
        return _executor.ExecuteAsync(
            connection => UpsertFileList(connection, content, capturedUtc),
            cancellationToken);
    }

    public Task<ClipboardImageUpsertResult> UpsertImageAsync(
        ClipboardImageContent content,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateImageContent(content);
        return _executor.ExecuteAsync(
            connection => UpsertImage(connection, content, capturedUtc),
            cancellationToken);
    }

    public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        return _executor.ExecuteAsync(
            connection => LoadReplayPayload(connection, entryId),
            cancellationToken);
    }

    public Task<ClipboardEntry?> SetPinnedAsync(
        Guid entryId,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        return _executor.ExecuteAsync(
            connection => SetPinned(connection, entryId, isPinned),
            cancellationToken);
    }

    public Task<bool> RemoveAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        return _executor.ExecuteAsync(
            connection => RemoveWithAssetClaims(connection, entryId).Removed,
            cancellationToken);
    }

    public Task<ClipboardRemovalResult> RemoveWithAssetClaimsAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        return _executor.ExecuteAsync(
            connection => RemoveWithAssetClaims(connection, entryId),
            cancellationToken);
    }

    public Task<IReadOnlyList<ClipboardAssetDeleteClaim>> LoadPendingAssetDeletesAsync(
        CancellationToken cancellationToken = default) =>
        _executor.ExecuteAsync(
            LoadPendingAssetDeletes,
            cancellationToken);

    public Task<ClipboardStoredAsset?> LoadAssetAsync(
        string assetHash,
        CancellationToken cancellationToken = default)
    {
        ValidateSemanticHash(assetHash, nameof(assetHash));
        return _executor.ExecuteAsync(
            connection => LoadAsset(connection, assetHash),
            cancellationToken);
    }

    public Task<bool> IsAssetDeleteClaimCurrentAsync(
        ClipboardAssetDeleteClaim claim,
        CancellationToken cancellationToken = default)
    {
        ValidateDeleteClaim(claim);
        return _executor.ExecuteAsync(
            connection => IsAssetDeleteClaimCurrent(connection, claim),
            cancellationToken);
    }

    public Task<bool> CompleteAssetDeleteAsync(
        ClipboardAssetDeleteClaim claim,
        CancellationToken cancellationToken = default)
    {
        ValidateDeleteClaim(claim);
        return _executor.ExecuteAsync(
            connection => CompleteAssetDelete(connection, claim),
            cancellationToken);
    }

    public Task<ClipboardAssetReference?> MarkAssetQuarantinedAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateSemanticHash(assetHash, nameof(assetHash));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return _executor.ExecuteAsync(
            connection => MarkAssetQuarantined(
                connection,
                assetHash,
                expectedRevision),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsExecutor)
        {
            _executor.Dispose();
        }
    }

    private static ClipboardPage LoadPage(
        SqliteConnection connection,
        ClipboardPageCursor? after,
        int pageSize)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM clipboard_entries;";
        var totalCount = Convert.ToInt32(
            countCommand.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = SelectEntrySql + " " + """
            WHERE $afterOrder IS NULL
               OR e.last_capture_order < $afterOrder
               OR (e.last_capture_order = $afterOrder AND e.id < $afterId)
            ORDER BY e.last_capture_order DESC, e.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$afterOrder",
            after is null ? DBNull.Value : after.Value.LastCaptureOrder);
        command.Parameters.AddWithValue(
            "$afterId",
            after is null ? DBNull.Value : after.Value.Id.ToString("D"));
        command.Parameters.AddWithValue("$limit", pageSize);

        var items = new List<ClipboardEntry>(Math.Min(totalCount, pageSize));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadEntry(reader));
        }

        ClipboardPageCursor? next = items.Count == pageSize
            ? new ClipboardPageCursor(items[^1].LastCaptureOrder, items[^1].Id)
            : null;
        return new ClipboardPage(items.AsReadOnly(), next, totalCount);
    }

    private static ClipboardEntry UpsertText(
        SqliteConnection connection,
        ClipboardTextContent content,
        DateTimeOffset capturedUtc)
    {
        using var transaction = connection.BeginTransaction();
        var existing = FindBySemanticHash(
            connection,
            transaction,
            ClipboardContentKind.Text,
            content.SemanticHash);
        var nextOrder = ReadNextCaptureOrder(connection, transaction);
        ClipboardEntry entry;
        if (existing is null)
        {
            entry = new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Text,
                content.SemanticHash,
                content.PlainPreview,
                capturedUtc,
                capturedUtc,
                nextOrder,
                CaptureCount: 1,
                IsPinned: false,
                Revision: 1);
            InsertEntry(connection, transaction, entry);
        }
        else
        {
            entry = existing with
            {
                PlainPreview = content.PlainPreview,
                LastCapturedUtc = capturedUtc,
                LastCaptureOrder = nextOrder,
                CaptureCount = checked(existing.CaptureCount + 1),
                Revision = checked(existing.Revision + 1),
            };
            UpdateCapturedEntry(connection, transaction, entry);
            DeleteRepresentations(connection, transaction, entry.Id);
        }

        InsertRepresentations(connection, transaction, entry.Id, content.Representations);
        transaction.Commit();
        return entry;
    }

    private static ClipboardEntry UpsertFileList(
        SqliteConnection connection,
        ClipboardFileListContent content,
        DateTimeOffset capturedUtc)
    {
        using var transaction = connection.BeginTransaction();
        var existing = FindBySemanticHash(
            connection,
            transaction,
            ClipboardContentKind.FileList,
            content.SemanticHash);
        var nextOrder = ReadNextCaptureOrder(connection, transaction);
        ClipboardEntry entry;
        if (existing is null)
        {
            entry = new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.FileList,
                content.SemanticHash,
                content.PlainPreview,
                capturedUtc,
                capturedUtc,
                nextOrder,
                CaptureCount: 1,
                IsPinned: false,
                Revision: 1,
                FileIntent: content.Intent,
                ItemCount: content.Files.Count);
            InsertEntry(connection, transaction, entry);
        }
        else
        {
            entry = existing with
            {
                PlainPreview = content.PlainPreview,
                LastCapturedUtc = capturedUtc,
                LastCaptureOrder = nextOrder,
                CaptureCount = checked(existing.CaptureCount + 1),
                Revision = checked(existing.Revision + 1),
                FileIntent = content.Intent,
                ItemCount = content.Files.Count,
            };
            UpdateCapturedEntry(connection, transaction, entry);
            DeleteFiles(connection, transaction, entry.Id);
        }

        InsertFiles(connection, transaction, entry.Id, content.Files);
        transaction.Commit();
        return entry;
    }

    private static ClipboardImageUpsertResult UpsertImage(
        SqliteConnection connection,
        ClipboardImageContent content,
        DateTimeOffset capturedUtc)
    {
        using var transaction = connection.BeginTransaction();
        var existing = FindBySemanticHash(
            connection,
            transaction,
            ClipboardContentKind.Image,
            content.SemanticHash);
        var previousAssetHashes = existing is null
            ? []
            : LoadEntryAssetHashes(connection, transaction, existing.Id);

        var primary = UpsertReadyAsset(
            connection,
            transaction,
            content.PrimaryPng,
            capturedUtc);
        var thumbnail = string.Equals(
                content.PrimaryPng.Hash,
                content.ThumbnailPng.Hash,
                StringComparison.Ordinal)
            ? primary
            : UpsertReadyAsset(
                connection,
                transaction,
                content.ThumbnailPng,
                capturedUtc);
        var image = new ClipboardImageMetadata(
            content.PixelWidth,
            content.PixelHeight,
            content.HasAlpha,
            content.DpiXMilli,
            content.DpiYMilli,
            primary,
            thumbnail);
        var nextOrder = ReadNextCaptureOrder(connection, transaction);
        ClipboardEntry entry;
        if (existing is null)
        {
            entry = new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Image,
                content.SemanticHash,
                $"{content.PixelWidth}×{content.PixelHeight}",
                capturedUtc,
                capturedUtc,
                nextOrder,
                CaptureCount: 1,
                IsPinned: false,
                Revision: 1,
                Image: image);
            InsertEntry(connection, transaction, entry);
        }
        else
        {
            entry = existing with
            {
                PlainPreview = $"{content.PixelWidth}×{content.PixelHeight}",
                LastCapturedUtc = capturedUtc,
                LastCaptureOrder = nextOrder,
                CaptureCount = checked(existing.CaptureCount + 1),
                Revision = checked(existing.Revision + 1),
                FileIntent = null,
                ItemCount = 0,
                Image = image,
            };
            UpdateCapturedEntry(connection, transaction, entry);
            DeleteRepresentations(connection, transaction, entry.Id);
            DeleteFiles(connection, transaction, entry.Id);
            DeleteAssetReferences(connection, transaction, entry.Id);
        }

        InsertAssetReference(
            connection,
            transaction,
            entry.Id,
            ClipboardAssetRole.PrimaryPng,
            primary.Hash);
        InsertAssetReference(
            connection,
            transaction,
            entry.Id,
            ClipboardAssetRole.ThumbnailPng,
            thumbnail.Hash);
        var pendingDeletes = MarkUnreferencedAssetsPending(
            connection,
            transaction,
            previousAssetHashes);
        transaction.Commit();
        return new ClipboardImageUpsertResult(
            entry,
            pendingDeletes.AsReadOnly());
    }

    private static ClipboardReplayPayload? LoadReplayPayload(
        SqliteConnection connection,
        Guid entryId)
    {
        var entry = FindById(connection, entryId);
        if (entry is null)
        {
            return null;
        }

        if (entry.Kind is ClipboardContentKind.Image)
        {
            return entry.FileIntent is null && entry.ItemCount == 0 && entry.Image is not null
                ? new ClipboardReplayPayload(
                    ClipboardContentKind.Image,
                    entry.SemanticHash,
                    [],
                    Image: entry.Image)
                : null;
        }

        if (entry.Kind is ClipboardContentKind.FileList)
        {
            return LoadFileReplayPayload(
                connection,
                entryId,
                entry.SemanticHash,
                entry.FileIntent,
                entry.ItemCount);
        }

        if (entry.Kind is not ClipboardContentKind.Text ||
            entry.FileIntent is not null ||
            entry.ItemCount != 0 ||
            entry.Image is not null)
        {
            return null;
        }

        using var representationsCommand = connection.CreateCommand();
        representationsCommand.CommandText = """
            SELECT representation_kind, fidelity_rank, inline_text, byte_length
            FROM clipboard_representations
            WHERE entry_id = $id
            ORDER BY fidelity_rank DESC, representation_kind;
            """;
        representationsCommand.Parameters.AddWithValue("$id", entryId.ToString("D"));
        var representations = new List<ClipboardStoredRepresentation>();
        using var representationsReader = representationsCommand.ExecuteReader();
        while (representationsReader.Read())
        {
            representations.Add(new ClipboardStoredRepresentation(
                (ClipboardRepresentationKind)representationsReader.GetInt32(0),
                representationsReader.GetInt32(1),
                representationsReader.GetString(2),
                representationsReader.GetInt32(3)));
        }

        return representations.Count == 0
            ? null
            : new ClipboardReplayPayload(
                entry.Kind,
                entry.SemanticHash,
                representations.AsReadOnly());
    }

    private static ClipboardReplayPayload? LoadFileReplayPayload(
        SqliteConnection connection,
        Guid entryId,
        string semanticHash,
        ClipboardFileTransferIntent? fileIntent,
        int itemCount)
    {
        if (fileIntent is null ||
            !Enum.IsDefined(fileIntent.Value) ||
            itemCount is <= 0 or >
                ClipboardFileNormalizationOptions.DefaultMaximumFileCount)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, original_path, canonical_path, display_name
            FROM clipboard_files
            WHERE entry_id = $id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        var files = new List<ClipboardFileReference>(itemCount);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new ClipboardFileReference(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        if (files.Count != itemCount ||
            files.Where((file, index) => file.Ordinal != index).Any() ||
            !AreValidFileReferences(files) ||
            !string.Equals(
                ClipboardFileSemanticIdentity.Compute(fileIntent.Value, files),
                semanticHash,
                StringComparison.Ordinal))
        {
            return null;
        }

        return new ClipboardReplayPayload(
            ClipboardContentKind.FileList,
            semanticHash,
            [],
            fileIntent,
            files.AsReadOnly());
    }

    private static ClipboardEntry? SetPinned(
        SqliteConnection connection,
        Guid entryId,
        bool isPinned)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE clipboard_entries
                SET is_pinned = $isPinned,
                    revision = revision + 1
                WHERE id = $id AND is_pinned <> $isPinned;
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
            _ = command.ExecuteNonQuery();
        }

        return FindById(connection, entryId);
    }

    private static ClipboardRemovalResult RemoveWithAssetClaims(
        SqliteConnection connection,
        Guid entryId)
    {
        using var transaction = connection.BeginTransaction();
        var assetHashes = LoadEntryAssetHashes(connection, transaction, entryId);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return new ClipboardRemovalResult(false, []);
            }
        }

        var pendingDeletes = MarkUnreferencedAssetsPending(
            connection,
            transaction,
            assetHashes);
        transaction.Commit();
        return new ClipboardRemovalResult(true, pendingDeletes.AsReadOnly());
    }

    private static IReadOnlyList<ClipboardAssetDeleteClaim> LoadPendingAssetDeletes(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.hash, a.relative_path, a.revision
            FROM content_assets AS a
            WHERE a.state = $pending
              AND NOT EXISTS (
                  SELECT 1
                  FROM clipboard_asset_refs AS r
                  WHERE r.asset_hash = a.hash)
            ORDER BY a.hash;
            """;
        command.Parameters.AddWithValue("$pending", (int)ClipboardAssetState.PendingDelete);
        var claims = new List<ClipboardAssetDeleteClaim>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            claims.Add(new ClipboardAssetDeleteClaim(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        return claims.AsReadOnly();
    }

    private static ClipboardStoredAsset? LoadAsset(
        SqliteConnection connection,
        string assetHash)
    {
        var stored = ReadStoredAsset(connection, transaction: null, assetHash);
        return stored is null
            ? null
            : new ClipboardStoredAsset(
                new ClipboardPreparedAsset(
                    stored.Hash,
                    stored.RelativePath,
                    stored.MediaType,
                    stored.ByteLength,
                    stored.PixelWidth,
                    stored.PixelHeight),
                stored.State,
                stored.Revision);
    }

    private static bool IsAssetDeleteClaimCurrent(
        SqliteConnection connection,
        ClipboardAssetDeleteClaim claim)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM content_assets AS a
            WHERE a.hash = $hash
              AND a.relative_path = $path
              AND a.revision = $revision
              AND a.state = $pending
              AND NOT EXISTS (
                  SELECT 1
                  FROM clipboard_asset_refs AS r
                  WHERE r.asset_hash = a.hash);
            """;
        command.Parameters.AddWithValue("$hash", claim.Hash);
        command.Parameters.AddWithValue("$path", claim.RelativePath);
        command.Parameters.AddWithValue("$revision", claim.Revision);
        command.Parameters.AddWithValue("$pending", (int)ClipboardAssetState.PendingDelete);
        return Convert.ToInt32(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) == 1;
    }

    private static bool CompleteAssetDelete(
        SqliteConnection connection,
        ClipboardAssetDeleteClaim claim)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM content_assets AS a
            WHERE a.hash = $hash
              AND a.relative_path = $path
              AND a.revision = $revision
              AND a.state = $pending
              AND NOT EXISTS (
                  SELECT 1
                  FROM clipboard_asset_refs AS r
                  WHERE r.asset_hash = a.hash);
            """;
        command.Parameters.AddWithValue("$hash", claim.Hash);
        command.Parameters.AddWithValue("$path", claim.RelativePath);
        command.Parameters.AddWithValue("$revision", claim.Revision);
        command.Parameters.AddWithValue("$pending", (int)ClipboardAssetState.PendingDelete);
        return command.ExecuteNonQuery() == 1;
    }

    private static ClipboardAssetReference? MarkAssetQuarantined(
        SqliteConnection connection,
        string assetHash,
        long expectedRevision)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE content_assets
                SET state = $quarantined,
                    revision = revision + 1
                WHERE hash = $hash
                  AND revision = $revision
                  AND state = $ready;
                """;
            command.Parameters.AddWithValue("$quarantined", (int)ClipboardAssetState.Quarantined);
            command.Parameters.AddWithValue("$hash", assetHash);
            command.Parameters.AddWithValue("$revision", expectedRevision);
            command.Parameters.AddWithValue("$ready", (int)ClipboardAssetState.Ready);
            _ = command.ExecuteNonQuery();
        }

        return ReadAssetReference(connection, transaction: null, assetHash);
    }

    private static ClipboardEntry? FindBySemanticHash(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardContentKind kind,
        string semanticHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectEntrySql +
            " WHERE e.kind = $kind AND e.semantic_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$hash", semanticHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static ClipboardEntry? FindById(SqliteConnection connection, Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectEntrySql + " WHERE e.id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static long ReadNextCaptureOrder(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(last_capture_order), 0) FROM clipboard_entries;";
        var maximum = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (maximum == long.MaxValue)
        {
            throw new InvalidOperationException("Clipboard capture order is exhausted.");
        }

        return maximum + 1;
    }

    private static void InsertEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_entries (
                id, kind, semantic_hash, plain_preview,
                first_captured_utc, last_captured_utc,
                last_capture_order, capture_count, is_pinned, revision,
                file_intent, item_count,
                pixel_width, pixel_height, has_alpha,
                dpi_x_milli, dpi_y_milli)
            VALUES (
                $id, $kind, $hash, $preview,
                $firstUtc, $lastUtc,
                $captureOrder, $captureCount, $isPinned, $revision,
                $fileIntent, $itemCount,
                $pixelWidth, $pixelHeight, $hasAlpha,
                $dpiXMilli, $dpiYMilli);
            """;
        AddEntryParameters(command, entry);
        command.ExecuteNonQuery();
    }

    private static void UpdateCapturedEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE clipboard_entries
            SET plain_preview = $preview,
                last_captured_utc = $lastUtc,
                last_capture_order = $captureOrder,
                capture_count = $captureCount,
                revision = $revision,
                file_intent = $fileIntent,
                item_count = $itemCount,
                pixel_width = $pixelWidth,
                pixel_height = $pixelHeight,
                has_alpha = $hasAlpha,
                dpi_x_milli = $dpiXMilli,
                dpi_y_milli = $dpiYMilli
            WHERE id = $id;
            """;
        AddEntryParameters(command, entry);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The clipboard entry disappeared during update.");
        }
    }

    private static void AddEntryParameters(SqliteCommand command, ClipboardEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$hash", entry.SemanticHash);
        command.Parameters.AddWithValue("$preview", entry.PlainPreview);
        command.Parameters.AddWithValue("$firstUtc", FormatUtc(entry.FirstCapturedUtc));
        command.Parameters.AddWithValue("$lastUtc", FormatUtc(entry.LastCapturedUtc));
        command.Parameters.AddWithValue("$captureOrder", entry.LastCaptureOrder);
        command.Parameters.AddWithValue("$captureCount", entry.CaptureCount);
        command.Parameters.AddWithValue("$isPinned", entry.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue(
            "$fileIntent",
            entry.FileIntent is null ? DBNull.Value : (int)entry.FileIntent.Value);
        command.Parameters.AddWithValue("$itemCount", entry.ItemCount);
        command.Parameters.AddWithValue(
            "$pixelWidth",
            entry.Image is null ? DBNull.Value : entry.Image.PixelWidth);
        command.Parameters.AddWithValue(
            "$pixelHeight",
            entry.Image is null ? DBNull.Value : entry.Image.PixelHeight);
        command.Parameters.AddWithValue(
            "$hasAlpha",
            entry.Image is null ? DBNull.Value : entry.Image.HasAlpha ? 1 : 0);
        command.Parameters.AddWithValue(
            "$dpiXMilli",
            entry.Image?.DpiXMilli is { } dpiXMilli ? dpiXMilli : DBNull.Value);
        command.Parameters.AddWithValue(
            "$dpiYMilli",
            entry.Image?.DpiYMilli is { } dpiYMilli ? dpiYMilli : DBNull.Value);
    }

    private static void DeleteRepresentations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clipboard_representations WHERE entry_id = $id;";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static void InsertRepresentations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        IEnumerable<ClipboardStoredRepresentation> representations)
    {
        foreach (var representation in representations)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO clipboard_representations (
                    entry_id, representation_kind, fidelity_rank,
                    inline_text, byte_length)
                VALUES ($id, $kind, $rank, $text, $byteLength);
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)representation.Kind);
            command.Parameters.AddWithValue("$rank", representation.FidelityRank);
            command.Parameters.AddWithValue("$text", representation.InlineText);
            command.Parameters.AddWithValue("$byteLength", representation.ByteLength);
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteFiles(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clipboard_files WHERE entry_id = $id;";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static void InsertFiles(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        IEnumerable<ClipboardFileReference> files)
    {
        foreach (var file in files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO clipboard_files (
                    entry_id, ordinal, original_path, canonical_path, display_name)
                VALUES ($id, $ordinal, $original, $canonical, $displayName);
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", file.Ordinal);
            command.Parameters.AddWithValue("$original", file.OriginalPath);
            command.Parameters.AddWithValue("$canonical", file.CanonicalPath);
            command.Parameters.AddWithValue("$displayName", file.DisplayName);
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteAssetReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clipboard_asset_refs WHERE entry_id = $id;";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static void InsertAssetReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        ClipboardAssetRole role,
        string assetHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_asset_refs (entry_id, role, asset_hash)
            VALUES ($id, $role, $hash);
            """;
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        command.Parameters.AddWithValue("$role", (int)role);
        command.Parameters.AddWithValue("$hash", assetHash);
        command.ExecuteNonQuery();
    }

    private static string[] LoadEntryAssetHashes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT asset_hash
            FROM clipboard_asset_refs
            WHERE entry_id = $id
            ORDER BY asset_hash;
            """;
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        var hashes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes.ToArray();
    }

    private static ClipboardAssetReference UpsertReadyAsset(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardPreparedAsset asset,
        DateTimeOffset createdUtc)
    {
        var existing = ReadStoredAsset(connection, transaction, asset.Hash);
        if (existing is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO content_assets (
                    hash, relative_path, media_type, byte_length,
                    pixel_width, pixel_height, state, revision, created_utc)
                VALUES (
                    $hash, $path, $mediaType, $byteLength,
                    $pixelWidth, $pixelHeight, $state, 1, $createdUtc);
                """;
            AddAssetParameters(insert, asset);
            insert.Parameters.AddWithValue("$state", (int)ClipboardAssetState.Ready);
            insert.Parameters.AddWithValue("$createdUtc", FormatUtc(createdUtc));
            insert.ExecuteNonQuery();
            return new ClipboardAssetReference(
                asset.Hash,
                ClipboardAssetState.Ready,
                Revision: 1);
        }

        if (!existing.Matches(asset))
        {
            throw new InvalidDataException(
                "An existing clipboard asset row conflicts with the verified file metadata.");
        }

        if (existing.State is ClipboardAssetState.Ready)
        {
            return new ClipboardAssetReference(asset.Hash, existing.State, existing.Revision);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE content_assets
                SET state = $ready,
                    revision = revision + 1
                WHERE hash = $hash AND revision = $revision;
                """;
            update.Parameters.AddWithValue("$ready", (int)ClipboardAssetState.Ready);
            update.Parameters.AddWithValue("$hash", asset.Hash);
            update.Parameters.AddWithValue("$revision", existing.Revision);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "The clipboard asset changed during Ready transition.");
            }
        }

        return new ClipboardAssetReference(
            asset.Hash,
            ClipboardAssetState.Ready,
            checked(existing.Revision + 1));
    }

    private static StoredAsset? ReadStoredAsset(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string assetHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT hash, relative_path, media_type, byte_length,
                   pixel_width, pixel_height, state, revision
            FROM content_assets
            WHERE hash = $hash
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", assetHash);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredAsset(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                (ClipboardAssetState)reader.GetInt32(6),
                reader.GetInt64(7))
            : null;
    }

    private static ClipboardAssetReference? ReadAssetReference(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string assetHash)
    {
        var asset = ReadStoredAsset(connection, transaction, assetHash);
        return asset is null
            ? null
            : new ClipboardAssetReference(asset.Hash, asset.State, asset.Revision);
    }

    private static List<ClipboardAssetDeleteClaim> MarkUnreferencedAssetsPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<string> assetHashes)
    {
        var claims = new List<ClipboardAssetDeleteClaim>();
        foreach (var hash in assetHashes.Distinct(StringComparer.Ordinal))
        {
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE content_assets
                    SET state = $pending,
                        revision = revision + 1
                    WHERE hash = $hash
                      AND state <> $pending
                      AND NOT EXISTS (
                          SELECT 1
                          FROM clipboard_asset_refs
                          WHERE asset_hash = $hash);
                    """;
                update.Parameters.AddWithValue("$pending", (int)ClipboardAssetState.PendingDelete);
                update.Parameters.AddWithValue("$hash", hash);
                _ = update.ExecuteNonQuery();
            }

            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT relative_path, revision
                FROM content_assets
                WHERE hash = $hash
                  AND state = $pending
                  AND NOT EXISTS (
                      SELECT 1
                      FROM clipboard_asset_refs
                      WHERE asset_hash = $hash)
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$hash", hash);
            select.Parameters.AddWithValue("$pending", (int)ClipboardAssetState.PendingDelete);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                claims.Add(new ClipboardAssetDeleteClaim(
                    hash,
                    reader.GetString(0),
                    reader.GetInt64(1)));
            }
        }

        return claims;
    }

    private static void AddAssetParameters(
        SqliteCommand command,
        ClipboardPreparedAsset asset)
    {
        command.Parameters.AddWithValue("$hash", asset.Hash);
        command.Parameters.AddWithValue("$path", asset.RelativePath);
        command.Parameters.AddWithValue("$mediaType", asset.MediaType);
        command.Parameters.AddWithValue("$byteLength", asset.ByteLength);
        command.Parameters.AddWithValue("$pixelWidth", asset.PixelWidth);
        command.Parameters.AddWithValue("$pixelHeight", asset.PixelHeight);
    }

    private static ClipboardEntry ReadEntry(SqliteDataReader reader)
    {
        var kind = (ClipboardContentKind)reader.GetInt32(1);
        ClipboardImageMetadata? image = null;
        if (kind is ClipboardContentKind.Image)
        {
            image = new ClipboardImageMetadata(
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                !reader.IsDBNull(14) && reader.GetInt32(14) != 0,
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                ReadAssetReference(reader, 17),
                ReadAssetReference(reader, 20));
        }

        return new ClipboardEntry(
            Guid.Parse(reader.GetString(0)),
            kind,
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8) != 0,
            reader.GetInt64(9),
            reader.IsDBNull(10)
                ? null
                : (ClipboardFileTransferIntent)reader.GetInt32(10),
            reader.GetInt32(11),
            image);
    }

    private static ClipboardAssetReference? ReadAssetReference(
        SqliteDataReader reader,
        int hashOrdinal)
    {
        if (reader.IsDBNull(hashOrdinal) ||
            reader.IsDBNull(hashOrdinal + 1) ||
            reader.IsDBNull(hashOrdinal + 2))
        {
            return null;
        }

        var hash = reader.GetString(hashOrdinal);
        var rawState = reader.GetInt32(hashOrdinal + 1);
        var revision = reader.GetInt64(hashOrdinal + 2);
        return IsLowercaseSha256(hash) &&
               Enum.IsDefined((ClipboardAssetState)rawState) &&
               revision > 0
            ? new ClipboardAssetReference(
                hash,
                (ClipboardAssetState)rawState,
                revision)
            : null;
    }

    private static void ValidateTextContent(ClipboardTextContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.SemanticHash.Length != 64 ||
            !content.SemanticHash.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "The semantic hash must be a lowercase SHA-256 hexadecimal value.",
                nameof(content));
        }

        ArgumentNullException.ThrowIfNull(content.PlainText);
        ArgumentNullException.ThrowIfNull(content.PlainPreview);
        ArgumentNullException.ThrowIfNull(content.Representations);
        if (content.Representations.Count == 0 ||
            !content.Representations.Any(static representation =>
                representation.Kind is ClipboardRepresentationKind.UnicodeText))
        {
            throw new ArgumentException(
                "Text content must contain a Unicode replay representation.",
                nameof(content));
        }

        if (!string.Equals(
                content.Representations.Single(static representation =>
                    representation.Kind is ClipboardRepresentationKind.UnicodeText).InlineText,
                content.PlainText,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Unicode representation must match the normalized plain text.",
                nameof(content));
        }

        var kinds = new HashSet<ClipboardRepresentationKind>();
        foreach (var representation in content.Representations)
        {
            ArgumentNullException.ThrowIfNull(representation);
            if (!kinds.Add(representation.Kind) ||
                representation.Kind is not (
                    ClipboardRepresentationKind.UnicodeText or
                    ClipboardRepresentationKind.Html or
                    ClipboardRepresentationKind.Rtf) ||
                representation.FidelityRank < 0 ||
                representation.ByteLength < 0)
            {
                throw new ArgumentException(
                    "Text representations must be unique, supported, and have non-negative metadata.",
                    nameof(content));
            }
        }
    }

    private static void ValidateFileListContent(ClipboardFileListContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateSemanticHash(content.SemanticHash, nameof(content));
        ArgumentNullException.ThrowIfNull(content.PlainPreview);
        ArgumentNullException.ThrowIfNull(content.Files);
        if (!Enum.IsDefined(content.Intent) ||
            !AreValidFileReferences(content.Files))
        {
            throw new ArgumentException(
                "File-list content requires a supported intent and valid file references.",
                nameof(content));
        }

        if (!string.Equals(
                ClipboardFileSemanticIdentity.Compute(content.Intent, content.Files),
                content.SemanticHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The file-list semantic hash does not match its paths and intent.",
                nameof(content));
        }
    }

    private static void ValidateImageContent(ClipboardImageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateSemanticHash(content.SemanticHash, nameof(content));
        ArgumentNullException.ThrowIfNull(content.PrimaryPng);
        ArgumentNullException.ThrowIfNull(content.ThumbnailPng);
        if (content.PixelWidth is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumDimensionPixels ||
            content.PixelHeight is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumDimensionPixels ||
            (long)content.PixelWidth * content.PixelHeight >
                ClipboardImageCaptureOptions.DefaultMaximumPixelCount ||
            content.DpiXMilli is <= 0 ||
            content.DpiYMilli is <= 0)
        {
            throw new ArgumentException(
                "Image content has invalid dimensions or DPI metadata.",
                nameof(content));
        }

        ValidatePreparedAsset(content.PrimaryPng, nameof(content));
        ValidatePreparedAsset(content.ThumbnailPng, nameof(content));
        if (content.PrimaryPng.PixelWidth != content.PixelWidth ||
            content.PrimaryPng.PixelHeight != content.PixelHeight ||
            content.ThumbnailPng.PixelWidth > content.PixelWidth ||
            content.ThumbnailPng.PixelHeight > content.PixelHeight ||
            Math.Max(
                content.ThumbnailPng.PixelWidth,
                content.ThumbnailPng.PixelHeight) >
                ClipboardImageCaptureOptions.DefaultThumbnailMaximumEdgePixels ||
            (string.Equals(
                 content.PrimaryPng.Hash,
                 content.ThumbnailPng.Hash,
                 StringComparison.Ordinal) &&
             content.PrimaryPng != content.ThumbnailPng))
        {
            throw new ArgumentException(
                "Image assets do not match the normalized image metadata.",
                nameof(content));
        }
    }

    private static void ValidatePreparedAsset(
        ClipboardPreparedAsset asset,
        string parameterName)
    {
        ValidateSemanticHash(asset.Hash, parameterName);
        var expectedPath = GetExpectedRelativeAssetPath(asset.Hash);
        if (!string.Equals(asset.RelativePath, expectedPath, StringComparison.Ordinal) ||
            !string.Equals(asset.MediaType, "image/png", StringComparison.Ordinal) ||
            asset.ByteLength is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumInputBytes ||
            asset.PixelWidth is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumDimensionPixels ||
            asset.PixelHeight is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumDimensionPixels)
        {
            throw new ArgumentException(
                "A prepared clipboard asset has invalid metadata.",
                parameterName);
        }
    }

    private static void ValidateDeleteClaim(ClipboardAssetDeleteClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateSemanticHash(claim.Hash, nameof(claim));
        ArgumentOutOfRangeException.ThrowIfLessThan(claim.Revision, 1);
        if (!string.Equals(
                claim.RelativePath,
                GetExpectedRelativeAssetPath(claim.Hash),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The asset deletion claim has an invalid relative path.",
                nameof(claim));
        }
    }

    private static bool AreValidFileReferences(
        IReadOnlyList<ClipboardFileReference> files)
    {
        if (files.Count is <= 0 or > ClipboardFileNormalizationOptions.DefaultMaximumFileCount)
        {
            return false;
        }

        long aggregateCharacters = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (file is null ||
                file.Ordinal != index ||
                string.IsNullOrWhiteSpace(file.OriginalPath) ||
                string.IsNullOrWhiteSpace(file.CanonicalPath) ||
                string.IsNullOrWhiteSpace(file.DisplayName) ||
                file.OriginalPath.Contains('\0', StringComparison.Ordinal) ||
                file.CanonicalPath.Contains('\0', StringComparison.Ordinal) ||
                file.DisplayName.Contains('\0', StringComparison.Ordinal) ||
                file.OriginalPath.Length >
                    ClipboardFileNormalizationOptions.DefaultMaximumPathCharacters ||
                file.CanonicalPath.Length >
                    ClipboardFileNormalizationOptions.DefaultMaximumPathCharacters ||
                !Path.IsPathFullyQualified(file.OriginalPath) ||
                !Path.IsPathFullyQualified(file.CanonicalPath))
            {
                return false;
            }

            if (!ClipboardFileDropCodec.TryNormalizePath(
                    file.CanonicalPath,
                    FileOptions,
                    out var normalized) ||
                !string.Equals(
                    normalized,
                    file.CanonicalPath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            aggregateCharacters += file.CanonicalPath.Length;
            if (aggregateCharacters >
                ClipboardFileNormalizationOptions.DefaultMaximumAggregatePathCharacters)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateSemanticHash(string semanticHash, string parameterName)
    {
        if (!IsLowercaseSha256(semanticHash))
        {
            throw new ArgumentException(
                "The semantic hash must be a lowercase SHA-256 hexadecimal value.",
                parameterName);
        }
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string GetExpectedRelativeAssetPath(string hash) =>
        $"{hash[..2]}/{hash}.png";

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string SelectEntrySql = """
        SELECT e.id, e.kind, e.semantic_hash, e.plain_preview,
               e.first_captured_utc, e.last_captured_utc,
               e.last_capture_order, e.capture_count, e.is_pinned, e.revision,
               e.file_intent, e.item_count,
               e.pixel_width, e.pixel_height, e.has_alpha,
               e.dpi_x_milli, e.dpi_y_milli,
               primary_asset.hash, primary_asset.state, primary_asset.revision,
               thumbnail_asset.hash, thumbnail_asset.state, thumbnail_asset.revision
        FROM clipboard_entries AS e
        LEFT JOIN clipboard_asset_refs AS primary_ref
          ON primary_ref.entry_id = e.id AND primary_ref.role = 0
        LEFT JOIN content_assets AS primary_asset
          ON primary_asset.hash = primary_ref.asset_hash
        LEFT JOIN clipboard_asset_refs AS thumbnail_ref
          ON thumbnail_ref.entry_id = e.id AND thumbnail_ref.role = 1
        LEFT JOIN content_assets AS thumbnail_asset
          ON thumbnail_asset.hash = thumbnail_ref.asset_hash
        """;

    private sealed record StoredAsset(
        string Hash,
        string RelativePath,
        string MediaType,
        long ByteLength,
        int PixelWidth,
        int PixelHeight,
        ClipboardAssetState State,
        long Revision)
    {
        public bool Matches(ClipboardPreparedAsset asset) =>
            string.Equals(Hash, asset.Hash, StringComparison.Ordinal) &&
            string.Equals(RelativePath, asset.RelativePath, StringComparison.Ordinal) &&
            string.Equals(MediaType, asset.MediaType, StringComparison.Ordinal) &&
            ByteLength == asset.ByteLength &&
            PixelWidth == asset.PixelWidth &&
            PixelHeight == asset.PixelHeight &&
            Enum.IsDefined(State) &&
            Revision > 0;
    }
}
