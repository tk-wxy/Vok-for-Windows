using System.Globalization;
using Microsoft.Data.Sqlite;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Persistence;

public sealed class SqliteClipboardHistoryRepository : IClipboardHistoryRepository, IDisposable
{
    private const int MaximumPageSize = 256;
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
            connection => Remove(connection, entryId),
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
        command.CommandText = """
            SELECT id, kind, semantic_hash, plain_preview,
                   first_captured_utc, last_captured_utc,
                   last_capture_order, capture_count, is_pinned, revision
            FROM clipboard_entries
            WHERE $afterOrder IS NULL
               OR last_capture_order < $afterOrder
               OR (last_capture_order = $afterOrder AND id < $afterId)
            ORDER BY last_capture_order DESC, id DESC
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

    private static ClipboardReplayPayload? LoadReplayPayload(
        SqliteConnection connection,
        Guid entryId)
    {
        ClipboardContentKind kind;
        string semanticHash;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kind, semantic_hash
                FROM clipboard_entries
                WHERE id = $id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            kind = (ClipboardContentKind)reader.GetInt32(0);
            semanticHash = reader.GetString(1);
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
            : new ClipboardReplayPayload(kind, semanticHash, representations.AsReadOnly());
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

    private static bool Remove(SqliteConnection connection, Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }

    private static ClipboardEntry? FindBySemanticHash(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardContentKind kind,
        string semanticHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectEntrySql + " WHERE kind = $kind AND semantic_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$hash", semanticHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static ClipboardEntry? FindById(SqliteConnection connection, Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectEntrySql + " WHERE id = $id LIMIT 1;";
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
                last_capture_order, capture_count, is_pinned, revision)
            VALUES (
                $id, $kind, $hash, $preview,
                $firstUtc, $lastUtc,
                $captureOrder, $captureCount, $isPinned, $revision);
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
                revision = $revision
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

    private static ClipboardEntry ReadEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        (ClipboardContentKind)reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.GetInt64(6),
        reader.GetInt32(7),
        reader.GetInt32(8) != 0,
        reader.GetInt64(9));

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

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string SelectEntrySql = """
        SELECT id, kind, semantic_hash, plain_preview,
               first_captured_utc, last_captured_utc,
               last_capture_order, capture_count, is_pinned, revision
        FROM clipboard_entries
        """;
}
