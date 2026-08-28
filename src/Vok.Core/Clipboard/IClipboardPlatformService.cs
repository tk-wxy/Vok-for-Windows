namespace Vok.Core.Clipboard;

public interface IClipboardPlatformService : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ClipboardCaptureResult> WatchAsync(
        CancellationToken cancellationToken = default);

    Task<ClipboardWriteReceipt> WriteAsync(
        ClipboardReplayPayload payload,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<bool> IsCurrentAsync(
        ClipboardWriteReceipt receipt,
        CancellationToken cancellationToken = default);

    ClipboardDiagnosticSnapshot GetDiagnosticSnapshot();
}
