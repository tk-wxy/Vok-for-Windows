namespace Vok.Core.Clipboard;

public interface IClipboardHistoryRepository
{
    Task<ClipboardPage> LoadPageAsync(
        ClipboardPageCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ClipboardEntry> UpsertTextAsync(
        ClipboardTextContent content,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default);

    Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);

    Task<ClipboardEntry?> SetPinnedAsync(
        Guid entryId,
        bool isPinned,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
}
