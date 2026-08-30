using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

public sealed partial class ClipboardViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 64;
    private readonly IClipboardHistoryRepository _repository;
    private readonly IClipboardFilePresentationService? _filePresentation;
    private readonly IClipboardThumbnailCache? _thumbnailCache;
    private readonly IClipboardImageAssetService? _imageAssets;
    private readonly bool _imageHistoryEnabled;
    private readonly string? _imageFeatureUnavailableStatus;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<ClipboardItemViewModel> _pendingFilePresentations = new();
    private ClipboardPageCursor? _nextCursor;
    private bool _initialized;
    private bool _disposed;
    private Guid? _pendingDeleteId;
    private bool _filePresentationPumpRunning;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isListenerReady;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _statusText = "正在加载剪贴板历史…";
    [ObservableProperty] private int _totalCount;

    public ClipboardViewModel(IClipboardHistoryRepository repository)
        : this(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: false,
            imageFeatureUnavailableStatus: null)
    {
    }

    public ClipboardViewModel(
        IClipboardHistoryRepository repository,
        IClipboardFilePresentationService? filePresentation)
        : this(
            repository,
            filePresentation,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: false,
            imageFeatureUnavailableStatus: null)
    {
    }

    internal ClipboardViewModel(
        IClipboardHistoryRepository repository,
        IClipboardFilePresentationService? filePresentation,
        IClipboardThumbnailCache? thumbnailCache,
        IClipboardImageAssetService? imageAssets,
        bool imageHistoryEnabled = false,
        string? imageFeatureUnavailableStatus = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _filePresentation = filePresentation;
        _thumbnailCache = thumbnailCache;
        _imageAssets = imageAssets;
        _imageHistoryEnabled = imageHistoryEnabled;
        _imageFeatureUnavailableStatus = imageFeatureUnavailableStatus;
    }

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = [];

    public bool HasMore => _nextCursor is not null;

    public bool IsEmpty => _initialized && !IsLoading && !HasError && Items.Count == 0;

    public string EmptyStateText => _imageHistoryEnabled
        ? "复制文本、文件或图片后会自动显示在这里。\n支持文本、HTML、RTF、文件系统路径和图片。"
        : _imageFeatureUnavailableStatus is null
            ? "复制文本或文件后会自动显示在这里。\n支持文本、HTML、RTF 和文件系统路径。"
            : $"复制文本或文件后会自动显示在这里。\n{_imageFeatureUnavailableStatus}";

    internal Func<ClipboardItemViewModel, Task>? PasteActionAsync { get; set; }

    internal Func<ClipboardItemViewModel, Task>? CopyFileActionAsync { get; set; }

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await LoadPageCoreAsync(reset: true).ConfigureAwait(true);
    }

    public async Task LoadMoreAsync()
    {
        ThrowIfDisposed();
        if (!_initialized || IsLoading || _nextCursor is null)
        {
            return;
        }

        await LoadPageCoreAsync(reset: false).ConfigureAwait(true);
    }

    public void ApplyCaptured(ClipboardEntry entry)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entry);
        ClearPendingDelete();
        var existing = Items.FirstOrDefault(item => item.Id == entry.Id);
        if (existing is not null)
        {
            Items.Remove(existing);
            existing.Update(entry);
            Items.Insert(0, existing);
        }
        else
        {
            var item = new ClipboardItemViewModel(entry);
            Items.Insert(0, item);
            QueueFilePresentation(item);
            if (entry.CaptureCount == 1)
            {
                TotalCount++;
            }
        }

        HasError = false;
        StatusText = "剪贴板监听已就绪。";
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void MarkListenerReady()
    {
        IsListenerReady = true;
        HasError = false;
        StatusText = _imageFeatureUnavailableStatus is not null
            ? $"剪贴板监听已就绪；{_imageFeatureUnavailableStatus}"
            : Items.Count == 0
                ? EmptyPromptText
                : "剪贴板监听已就绪。";
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void MarkListenerFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        IsListenerReady = false;
        HasError = true;
        StatusText = $"剪贴板监听不可用：{exception.Message}";
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void RecordCaptureFault(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        StatusText = $"最近一次剪贴板捕获失败：{message}";
    }

    public void SetActionStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        StatusText = message;
    }

    internal Task<ClipboardThumbnailPresentation>? LoadThumbnailAsync(
        ClipboardItemViewModel item,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        var thumbnail = item.Entry.Image?.ThumbnailPng;
        if (!item.IsImage ||
            thumbnail is null ||
            thumbnail.State is not ClipboardAssetState.Ready ||
            _thumbnailCache is null)
        {
            return null;
        }

        return LoadThumbnailCoreAsync(
            thumbnail.Hash,
            thumbnail.Revision,
            cancellationToken);
    }

    private async Task<ClipboardThumbnailPresentation> LoadThumbnailCoreAsync(
        string assetHash,
        long assetRevision,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        return await _thumbnailCache!
            .LoadAsync(assetHash, assetRevision, linkedCancellation.Token)
            .ConfigureAwait(true);
    }

    public async Task RemoveAsync(ClipboardItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            bool removed;
            if (item.IsImage)
            {
                if (_imageAssets is null)
                {
                    StatusText = "图片资产服务尚未启用，无法安全删除此条历史。";
                    return;
                }

                removed = await _imageAssets
                    .RemoveAsync(item.Id, _lifetime.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                removed = await _repository
                    .RemoveAsync(item.Id, _lifetime.Token)
                    .ConfigureAwait(true);
            }

            if (removed)
            {
                Items.Remove(item);
                TotalCount = Math.Max(0, TotalCount - 1);
                StatusText = "已删除剪贴板历史；系统剪贴板内容未改变。";
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordOperationFault("删除失败", exception);
        }
    }

    public async Task RequestRemoveAsync(ClipboardItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (_pendingDeleteId != item.Id)
        {
            ClearPendingDelete();
            _pendingDeleteId = item.Id;
            item.SetDeleteArmed(true);
            StatusText = "再次点击删除按钮以确认；只删除 Vok 历史。";
            return;
        }

        ClearPendingDelete();
        await RemoveAsync(item).ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        PasteActionAsync = null;
        CopyFileActionAsync = null;
        _lifetime.Dispose();
    }

    [RelayCommand]
    private async Task PasteAsync(ClipboardItemViewModel? item)
    {
        if (item is null || PasteActionAsync is null)
        {
            return;
        }

        ClearPendingDelete();
        await PasteActionAsync(item).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyFileAsync(ClipboardItemViewModel? item)
    {
        if (item is null || !item.IsFileList || CopyFileActionAsync is null)
        {
            return;
        }

        ClearPendingDelete();
        await CopyFileActionAsync(item).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TogglePinnedAsync(ClipboardItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ClearPendingDelete();
        try
        {
            var updated = await _repository
                .SetPinnedAsync(item.Id, !item.Entry.IsPinned, _lifetime.Token)
                .ConfigureAwait(true);
            if (updated is not null)
            {
                item.Update(updated);
                StatusText = updated.IsPinned ? "已固定此条历史。" : "已取消固定。";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordOperationFault("固定状态更新失败", exception);
        }
    }

    private async Task LoadPageCoreAsync(bool reset)
    {
        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var page = await _repository
                .LoadPageAsync(reset ? null : _nextCursor, PageSize, _lifetime.Token)
                .ConfigureAwait(true);
            if (reset)
            {
                Items.Clear();
            }

            foreach (var entry in page.Items)
            {
                if (Items.All(item => item.Id != entry.Id))
                {
                    var item = new ClipboardItemViewModel(entry);
                    Items.Add(item);
                    QueueFilePresentation(item);
                }
            }

            _nextCursor = page.NextCursor;
            TotalCount = page.TotalCount;
            HasError = false;
            StatusText = Items.Count == 0
                ? EmptyPromptText
                : $"已加载 {Items.Count} 条剪贴板历史。";
            OnPropertyChanged(nameof(HasMore));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"剪贴板历史加载失败：{exception.Message}";
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private string EmptyPromptText => _imageHistoryEnabled
        ? "复制文本、文件或图片后会自动显示在这里。"
        : _imageFeatureUnavailableStatus ?? "复制文本或文件后会自动显示在这里。";

    private void RecordOperationFault(string operation, Exception exception)
    {
        StatusText = $"{operation}：{exception.Message}";
    }

    private void ClearPendingDelete()
    {
        if (_pendingDeleteId is { } pendingId)
        {
            Items.FirstOrDefault(item => item.Id == pendingId)?.SetDeleteArmed(false);
            _pendingDeleteId = null;
        }
    }

    private void QueueFilePresentation(ClipboardItemViewModel item)
    {
        if (!item.IsFileList || _filePresentation is null)
        {
            return;
        }

        _pendingFilePresentations.Enqueue(item);
        if (_filePresentationPumpRunning)
        {
            return;
        }

        _filePresentationPumpRunning = true;
        _ = PumpFilePresentationsAsync();
    }

    private async Task PumpFilePresentationsAsync()
    {
        try
        {
            while (!_disposed && _pendingFilePresentations.TryDequeue(out var item))
            {
                try
                {
                    var payload = await _repository
                        .GetReplayPayloadAsync(item.Id, _lifetime.Token)
                        .ConfigureAwait(true);
                    if (payload is null || payload.Kind is not ClipboardContentKind.FileList ||
                        payload.FileReferences.Count == 0)
                    {
                        item.MarkFilePresentationUnavailable();
                        continue;
                    }

                    var presentation = await _filePresentation!
                        .InspectAsync(payload.FileReferences, 48, _lifetime.Token)
                        .ConfigureAwait(true);
                    if (!_disposed && Items.Contains(item))
                    {
                        item.ApplyFilePresentation(
                            payload,
                            presentation,
                            presentation.IconPngBytes is null
                                ? null
                                : DecodeIcon(presentation.IconPngBytes));
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (!_disposed && Items.Contains(item))
                    {
                        item.MarkFilePresentationUnavailable();
                    }

                    Trace.TraceWarning(
                        "Clipboard file presentation failed with {0}.",
                        exception.GetType().Name);
                }
            }
        }
        finally
        {
            _filePresentationPumpRunning = false;
            if (!_disposed && _pendingFilePresentations.Count != 0)
            {
                _filePresentationPumpRunning = true;
                _ = PumpFilePresentationsAsync();
            }
        }
    }

    private static BitmapImage DecodeIcon(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
