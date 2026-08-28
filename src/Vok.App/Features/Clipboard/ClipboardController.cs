using System.Diagnostics;
using Vok.Core.Clipboard;
using Vok.Hosting;

namespace Vok.Features.Clipboard;

public sealed class ClipboardController : IDisposable
{
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);
    private readonly IClipboardPlatformService _platform;
    private readonly IClipboardCaptureNormalizer _normalizer;
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
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _workspaceWindow = workspaceWindow ?? throw new ArgumentNullException(nameof(workspaceWindow));
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
        _platform.Dispose();
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
                if (capture.Disposition is ClipboardCaptureDisposition.Failed)
                {
                    var message = capture.Error ?? "Clipboard capture failed.";
                    _workspaceWindow.PostNormal(() =>
                        _viewModel.RecordCaptureFault(message));
                    continue;
                }

                var content = _normalizer.NormalizeText(capture);
                if (content is null)
                {
                    continue;
                }

                var entry = await _repository
                    .UpsertTextAsync(content, capture.CapturedUtc, _lifetime.Token)
                    .ConfigureAwait(false);
                _workspaceWindow.PostNormal(() => _viewModel.ApplyCaptured(entry));
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
