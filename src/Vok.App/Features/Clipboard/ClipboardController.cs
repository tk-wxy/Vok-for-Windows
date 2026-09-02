using System.Diagnostics;
using System.IO;
using Vok.Core.Clipboard;
using Vok.Hosting;

namespace Vok.Features.Clipboard;

public sealed class ClipboardController : IDisposable
{
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);
    private readonly IClipboardPlatformService _platform;
    private readonly IClipboardCaptureNormalizer _normalizer;
    private readonly IClipboardImageCaptureService? _imageCapture;
    private readonly IClipboardHistoryMaintenanceService? _maintenance;
    private readonly ClipboardRetentionOptions _retentionOptions;
    private readonly IClipboardHistoryRepository _repository;
    private readonly ClipboardViewModel _viewModel;
    private readonly IWorkspaceWindow _workspaceWindow;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _watchTask;
    private bool _disposed;

    public ClipboardController(
        IClipboardPlatformService platform,
        IClipboardCaptureNormalizer normalizer,
        IClipboardHistoryRepository repository,
        ClipboardViewModel viewModel,
        IWorkspaceWindow workspaceWindow)
        : this(
            platform,
            normalizer,
            repository,
            viewModel,
            workspaceWindow,
            imageCapture: null,
            maintenance: repository as IClipboardHistoryMaintenanceService,
            retentionOptions: null)
    {
    }

    public ClipboardController(
        IClipboardPlatformService platform,
        IClipboardCaptureNormalizer normalizer,
        IClipboardHistoryRepository repository,
        ClipboardViewModel viewModel,
        IWorkspaceWindow workspaceWindow,
        IClipboardImageCaptureService? imageCapture,
        IClipboardHistoryMaintenanceService? maintenance = null,
        ClipboardRetentionOptions? retentionOptions = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _workspaceWindow = workspaceWindow ?? throw new ArgumentNullException(nameof(workspaceWindow));
        _imageCapture = imageCapture;
        _maintenance = maintenance ?? repository as IClipboardHistoryMaintenanceService;
        _retentionOptions = retentionOptions ?? new ClipboardRetentionOptions();
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watchTask is not null)
        {
            return;
        }

        try
        {
            await _platform.StartAsync(_lifetime.Token).ConfigureAwait(true);
            if (_maintenance is not null)
            {
                try
                {
                    var maintenance = await _maintenance
                        .ApplyRetentionAsync(
                            _retentionOptions,
                            DateTimeOffset.UtcNow,
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    _viewModel.ApplyMaintenance(maintenance);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Trace.TraceWarning(
                        "Clipboard startup retention failed: {0}",
                        exception);
                    _viewModel.RecordCaptureFault(
                        "自动清理暂不可用；既有历史未被静默删除。");
                }
            }

            _viewModel.MarkListenerReady();
            _watchTask = WatchAsync();
        }
        catch (Exception exception)
        {
            _viewModel.MarkListenerFault(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            if (_watchTask is not null &&
                !_watchTask.Wait(ShutdownWatchdog))
            {
                Trace.TraceWarning("Clipboard capture loop did not stop within its shutdown watchdog.");
            }
        }
        catch (AggregateException exception)
        {
            Trace.TraceWarning("Clipboard capture loop ended with a shutdown fault: {0}", exception);
        }

        _platform.Dispose();
        _lifetime.Dispose();
    }

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var capture in _platform
                               .WatchAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                var historyEpoch = _viewModel.CaptureHistoryEpoch();
                if (IsImageCapture(capture))
                {
                    await ProcessImageCaptureAsync(capture, historyEpoch)
                        .ConfigureAwait(false);
                    continue;
                }

                using (capture)
                {
                    if (capture.Disposition is ClipboardCaptureDisposition.Failed)
                    {
                        var message = capture.Error ?? "Clipboard capture failed.";
                        _workspaceWindow.PostNormal(() =>
                            _viewModel.RecordCaptureFault(message));
                        continue;
                    }

                    var content = _normalizer.Normalize(capture);
                    if (content is null)
                    {
                        continue;
                    }

                    var entry = content switch
                    {
                        ClipboardTextContent text => await _repository
                            .UpsertTextAsync(text, capture.CapturedUtc, _lifetime.Token)
                            .ConfigureAwait(false),
                        ClipboardFileListContent files => await _repository
                            .UpsertFileListAsync(files, capture.CapturedUtc, _lifetime.Token)
                            .ConfigureAwait(false),
                        _ => null,
                    };
                    if (entry is null)
                    {
                        continue;
                    }

                    var maintenance = await ApplyRetentionAsync().ConfigureAwait(false);
                    if (!await KeepCommittedCaptureAsync(entry.Id, historyEpoch)
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    _workspaceWindow.PostNormal(() =>
                    {
                        if (!_viewModel.IsHistoryEpochCurrent(historyEpoch))
                        {
                            return;
                        }

                        _viewModel.ApplyCaptured(entry, historyEpoch);
                        if (maintenance is not null)
                        {
                            _viewModel.ApplyMaintenance(maintenance);
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PostFault(exception);
        }
    }

    private async Task ProcessImageCaptureAsync(
        ClipboardCaptureResult capture,
        long historyEpoch)
    {
        if (_imageCapture is null)
        {
            try
            {
                capture.Dispose();
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "A disabled clipboard image capture did not release cleanly: {0}",
                    exception);
            }

            PostCaptureFault("图片捕获服务尚未启用，未保存此条图片。");
            return;
        }

        try
        {
            var entry = await _imageCapture
                .CaptureAsync(capture, _lifetime.Token)
                .ConfigureAwait(false);
            if (entry.Kind is not ClipboardContentKind.Image || entry.Image is null)
            {
                throw new InvalidDataException(
                    "The clipboard image pipeline returned a non-image entry.");
            }

            var maintenance = await ApplyRetentionAsync().ConfigureAwait(false);
            if (!await KeepCommittedCaptureAsync(entry.Id, historyEpoch)
                    .ConfigureAwait(false))
            {
                return;
            }

            _workspaceWindow.PostNormal(() =>
            {
                if (!_viewModel.IsHistoryEpochCurrent(historyEpoch))
                {
                    return;
                }

                _viewModel.ApplyCaptured(entry, historyEpoch);
                if (maintenance is not null)
                {
                    _viewModel.ApplyMaintenance(maintenance);
                }
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Clipboard image capture failed: {0}", exception);
            PostCaptureFault("图片捕获失败，未保存此条图片。");
        }
    }

    private async Task<ClipboardHistoryMaintenanceResult?> ApplyRetentionAsync()
    {
        if (_maintenance is null)
        {
            return null;
        }

        try
        {
            return await _maintenance
                .ApplyRetentionAsync(
                    _retentionOptions,
                    DateTimeOffset.UtcNow,
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Clipboard retention failed after capture: {0}", exception);
            PostCaptureFault("内容已保存，但自动清理暂不可用。");
            return null;
        }
    }

    private async Task<bool> KeepCommittedCaptureAsync(
        Guid entryId,
        long historyEpoch)
    {
        if (_viewModel.IsHistoryEpochCurrent(historyEpoch))
        {
            return true;
        }

        if (_maintenance is not null)
        {
            try
            {
                _ = await _maintenance
                    .RemoveEntryAsync(entryId, _lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "A clipboard capture overlapped clear-history and could not be removed: {0}",
                    exception);
                PostCaptureFault("清空期间完成的捕获未能安全收尾，请再次清空历史。");
            }
        }

        return false;
    }

    private void PostCaptureFault(string message)
    {
        try
        {
            _workspaceWindow.PostNormal(() => _viewModel.RecordCaptureFault(message));
        }
        catch (Exception dispatchException)
        {
            Trace.TraceWarning(
                "Unable to publish a clipboard capture failure during shutdown: {0}",
                dispatchException);
        }
    }

    private static bool IsImageCapture(ClipboardCaptureResult capture) =>
        capture.Disposition is ClipboardCaptureDisposition.Captured &&
        capture.Formats.Any(static format =>
            format.RepresentationKind is
                ClipboardRepresentationKind.Png or
                ClipboardRepresentationKind.DibV5 or
                ClipboardRepresentationKind.Dib or
                ClipboardRepresentationKind.Bitmap);

    private void PostFault(Exception exception)
    {
        try
        {
            _workspaceWindow.PostNormal(() => _viewModel.MarkListenerFault(exception));
        }
        catch (Exception dispatchException)
        {
            Trace.TraceWarning(
                "Unable to publish clipboard listener failure during shutdown: {0}",
                dispatchException);
        }
    }
}
