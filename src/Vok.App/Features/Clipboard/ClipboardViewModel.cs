using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

public sealed partial class ClipboardViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 64;
    private readonly IClipboardHistoryRepository _repository;
    private readonly CancellationTokenSource _lifetime = new();
    private ClipboardPageCursor? _nextCursor;
    private bool _initialized;
    private bool _disposed;
    private Guid? _pendingDeleteId;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isListenerReady;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _statusText = "正在加载剪贴板历史…";
    [ObservableProperty] private int _totalCount;

    public ClipboardViewModel(IClipboardHistoryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = [];

    public bool HasMore => _nextCursor is not null;

    public bool IsEmpty => _initialized && !IsLoading && !HasError && Items.Count == 0;

    internal Func<ClipboardItemViewModel, Task>? PasteActionAsync { get; set; }

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
            Items.Insert(0, new ClipboardItemViewModel(entry));
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
        StatusText = Items.Count == 0
            ? "复制文本后会自动显示在这里。"
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

    public async Task RemoveAsync(ClipboardItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (await _repository
                    .RemoveAsync(item.Id, _lifetime.Token)
                    .ConfigureAwait(true))
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
                    Items.Add(new ClipboardItemViewModel(entry));
                }
            }

            _nextCursor = page.NextCursor;
            TotalCount = page.TotalCount;
            HasError = false;
            StatusText = Items.Count == 0
                ? "复制文本后会自动显示在这里。"
                : $"已加载 {Items.Count} 条文本历史。";
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
}
