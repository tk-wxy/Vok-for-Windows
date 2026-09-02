using System.Diagnostics;
using System.Windows.Threading;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Persistence;

namespace Vok.Features.Clipboard;

internal sealed class ClipboardImageFeature : IDisposable
{
    private const string DisabledStatus = "图片历史暂不可用；文本和文件仍可使用。";
    private readonly WindowsClipboardImageExecutor? _normalizer;
    private readonly ClipboardImageAssetCoordinator? _assets;
    private readonly ClipboardThumbnailCache? _thumbnailCache;
    private int _disposed;

    private ClipboardImageFeature(
        WindowsClipboardImageExecutor? normalizer,
        ClipboardImageAssetCoordinator? assets,
        ClipboardThumbnailCache? thumbnailCache,
        IClipboardImageCaptureService? captureService,
        string? unavailableStatus)
    {
        _normalizer = normalizer;
        _assets = assets;
        _thumbnailCache = thumbnailCache;
        CaptureService = captureService;
        UnavailableStatus = unavailableStatus;
    }

    public bool IsEnabled => CaptureService is not null;

    public IClipboardImageCaptureService? CaptureService { get; }

    public string? UnavailableStatus { get; }

    public IClipboardImageAssetService? AssetService => _assets;

    public IClipboardHistoryMaintenanceService? MaintenanceService => _assets;

    public IClipboardImageReplayMaterializer? ReplayMaterializer => _assets;

    public IClipboardImageExportMaterializer? ExportMaterializer => _assets;

    public IClipboardThumbnailCache? ThumbnailCache => _thumbnailCache;

    public static ClipboardImageFeature Create(
        SqliteClipboardHistoryRepository repository,
        Dispatcher dispatcher,
        ClipboardImageCaptureOptions options)
        => Create(
            repository,
            dispatcher,
            options,
            () => new WindowsClipboardImageExecutor(options),
            () => ContentAddressedClipboardAssetStore.CreateDefault(
                options,
                ReportCleanupFault));

    internal static ClipboardImageFeature Create(
        SqliteClipboardHistoryRepository repository,
        Dispatcher dispatcher,
        ClipboardImageCaptureOptions options,
        Func<WindowsClipboardImageExecutor> normalizerFactory,
        Func<ContentAddressedClipboardAssetStore> assetStoreFactory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(normalizerFactory);
        ArgumentNullException.ThrowIfNull(assetStoreFactory);
        dispatcher.VerifyAccess();

        WindowsClipboardImageExecutor? normalizer = null;
        ContentAddressedClipboardAssetStore? assetStore = null;
        ClipboardImageAssetCoordinator? assets = null;
        ClipboardThumbnailCache? thumbnailCache = null;
        try
        {
            normalizer = normalizerFactory();
            assetStore = assetStoreFactory();
            assets = new ClipboardImageAssetCoordinator(
                repository,
                assetStore,
                options,
                ReportCleanupFault);
            assetStore = null;
            thumbnailCache = new ClipboardThumbnailCache(
                dispatcher,
                assets,
                options);
            var captureService = new WindowsClipboardImageCaptureService(
                normalizer,
                assets);
            return new ClipboardImageFeature(
                normalizer,
                assets,
                thumbnailCache,
                captureService,
                unavailableStatus: null);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Clipboard image history is unavailable; text and file history remain enabled: {0}",
                exception);
            DisposePartial(thumbnailCache, assets, assetStore, normalizer);
            return new ClipboardImageFeature(
                normalizer: null,
                assets: null,
                thumbnailCache: null,
                captureService: null,
                DisabledStatus);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposePartial(
            _thumbnailCache,
            _assets,
            assetStore: null,
            _normalizer);
    }

    private static void DisposePartial(
        ClipboardThumbnailCache? thumbnailCache,
        ClipboardImageAssetCoordinator? assets,
        ContentAddressedClipboardAssetStore? assetStore,
        WindowsClipboardImageExecutor? normalizer)
    {
        TryDispose(thumbnailCache, "thumbnail cache");
        TryDispose(assets, "asset coordinator");
        TryDispose(assetStore, "unclaimed asset store");
        TryDispose(normalizer, "normalization worker");
    }

    private static void TryDispose(IDisposable? resource, string name)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Clipboard image {0} did not dispose cleanly: {1}",
                name,
                exception);
        }
    }

    private static void ReportCleanupFault(Exception exception) =>
        Trace.TraceWarning("Clipboard image resource cleanup failed: {0}", exception);
}
