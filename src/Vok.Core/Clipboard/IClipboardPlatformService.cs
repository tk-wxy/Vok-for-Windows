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

    Task<ClipboardWriteReceipt> WriteImageAsync(
        ClipboardImageReplayMaterialization materialization,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ClipboardWriteReceipt>(
            new NotSupportedException("Clipboard image replay is not available."));

    Task<bool> IsCurrentAsync(
        ClipboardWriteReceipt receipt,
        CancellationToken cancellationToken = default);

    ClipboardDiagnosticSnapshot GetDiagnosticSnapshot();
}
