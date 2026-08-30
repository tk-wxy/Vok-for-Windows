using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class WindowsClipboardImageCaptureService : IClipboardImageCaptureService
{
    private readonly WindowsClipboardImageExecutor _normalizer;
    private readonly ClipboardImageAssetCoordinator _assets;

    public WindowsClipboardImageCaptureService(
        WindowsClipboardImageExecutor normalizer,
        ClipboardImageAssetCoordinator assets)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    public async Task<ClipboardEntry> CaptureAsync(
        ClipboardCaptureResult capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var capturedUtc = capture.CapturedUtc;
        var normalized = await _normalizer
            .NormalizeAsync(capture, cancellationToken)
            .ConfigureAwait(false);

        Task<ClipboardEntry> commit;
        try
        {
            commit = _assets.CommitAsync(
                normalized,
                capturedUtc,
                cancellationToken);
        }
        catch
        {
            normalized.Dispose();
            throw;
        }

        return await commit.ConfigureAwait(false);
    }
}
