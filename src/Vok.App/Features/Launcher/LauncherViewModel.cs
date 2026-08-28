using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using Vok.Core.Launcher;

namespace Vok.Features.Launcher;

public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    private const int ColumnCount = 4;
    private const int PageSize = 256;
    private const int IconPixelSize = 96;

    private readonly ILauncherRepository _repository;
    private readonly ILauncherShellService _shell;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<LauncherItemViewModel> _pendingIcons = new();
    private bool _iconPumpRunning;
    private bool _initialized;
    private bool _disposed;
    private long? _nextSortOrder;
    private int _totalCount;
    private long _nextReorderGeneration;
    private LauncherReorderSession? _activeReorder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "拖入文件、应用或文件夹，即可固定到这里。";

    [ObservableProperty]
    private bool _isCatalogBusy;

    [ObservableProperty]
    private bool _isReordering;

    public LauncherViewModel(
        ILauncherRepository repository,
        ILauncherShellService shell)
    {
        _repository = repository;
        _shell = shell;
    }

    public ObservableCollection<LauncherItemViewModel> Items { get; } = [];

    public ObservableCollection<LauncherRowViewModel> Rows { get; } = [];

    public int Count => _totalCount;

    public bool IsEmpty => _totalCount == 0;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    internal Func<LauncherItemViewModel, Task>? OpenActionAsync { get; set; }

    internal Func<LauncherItemViewModel, Task>? RevealActionAsync { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        await _catalogGate.WaitAsync(operationToken).ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            IsCatalogBusy = true;
            Items.Clear();
            var page = await _repository
                .LoadPageAsync(
                    afterSortOrder: null,
                    pageSize: PageSize,
                    operationToken)
                .ConfigureAwait(true);
            foreach (var item in page.Items)
            {
                Items.Add(new LauncherItemViewModel(item));
            }

            _nextSortOrder = page.NextSortOrder;
            _totalCount = page.TotalCount;
            _initialized = true;
            RebuildRows();
            QueueMissingIcons();
            StatusText = _totalCount == 0
                ? "拖入文件、应用或文件夹，即可固定到这里。"
                : $"已加载 {_totalCount} 个启动项。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError("启动台加载失败", exception);
        }
        finally
        {
            IsCatalogBusy = false;
            _catalogGate.Release();
        }
    }

    public async Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        await _catalogGate.WaitAsync(operationToken).ConfigureAwait(true);
        try
        {
            if (!_initialized || _nextSortOrder is null || IsReordering)
            {
                return;
            }

            IsCatalogBusy = true;
            var page = await _repository
                .LoadPageAsync(_nextSortOrder, PageSize, operationToken)
                .ConfigureAwait(true);
            foreach (var model in page.Items)
            {
                var item = new LauncherItemViewModel(model);
                Items.Add(item);
                QueueIcon(item);
            }

            _nextSortOrder = page.NextSortOrder;
            _totalCount = page.TotalCount;
            RebuildRows();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError("加载更多启动项失败", exception);
        }
        finally
        {
            IsCatalogBusy = false;
            _catalogGate.Release();
        }
    }

    public async Task AddDroppedPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        var snapshot = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _catalogGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateAcquired = true;
            IsCatalogBusy = true;
            StatusText = $"正在添加 {snapshot.Length} 个目标…";
            var resolutions = await _shell
                .ResolveAsync(snapshot, operationToken)
                .ConfigureAwait(true);
            var targets = resolutions
                .Where(static result => result.Target is not null)
                .Select(static result => result.Target!)
                .ToArray();
            var failedCount = resolutions.Count(static result => !result.Succeeded);
            if (targets.Length == 0)
            {
                var firstError = resolutions.FirstOrDefault(static result => !result.Succeeded)?.Error;
                StatusText = string.IsNullOrWhiteSpace(firstError)
                    ? "没有可添加的文件系统目标。"
                    : $"未能添加目标：{firstError}";
                return;
            }

            var result = await _repository
                .AddAsync(targets, operationToken)
                .ConfigureAwait(true);
            var allItemsWereLoaded = _nextSortOrder is null;
            _totalCount = checked(_totalCount + result.Added.Count);
            if (allItemsWereLoaded)
            {
                foreach (var added in result.Added)
                {
                    var item = new LauncherItemViewModel(added);
                    Items.Add(item);
                    QueueIcon(item);
                }
            }

            RebuildRows();
            StatusText = CreateAddStatus(result, failedCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError("添加启动项失败", exception);
        }
        finally
        {
            IsCatalogBusy = false;
            if (gateAcquired)
            {
                _catalogGate.Release();
            }
        }
    }

    public void RecordError(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        StatusText = $"{operation}：{exception.Message}";
    }

    internal LauncherReorderSession? BeginReorder(LauncherItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        if (IsCatalogBusy || IsReordering || item.IsBusy || Items.Count < 2)
        {
            return null;
        }

        var sourceIndex = Items.IndexOf(item);
        if (sourceIndex < 0)
        {
            return null;
        }

        var session = new LauncherReorderSession(
            Interlocked.Increment(ref _nextReorderGeneration),
            item,
            sourceIndex,
            Items.ToArray());
        _activeReorder = session;
        IsReordering = true;
        item.IsDragging = true;
        return session;
    }

    internal bool PreviewReorder(LauncherReorderSession session, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_activeReorder, session) ||
            session.State is not LauncherReorderSessionState.Previewing ||
            Items.Count == 0)
        {
            return false;
        }

        var boundedIndex = Math.Clamp(targetIndex, 0, Items.Count - 1);
        var currentIndex = Items.IndexOf(session.Item);
        if (currentIndex < 0 || currentIndex == boundedIndex)
        {
            return false;
        }

        Items.Move(currentIndex, boundedIndex);
        session.CurrentIndex = boundedIndex;
        RebuildRows();
        return true;
    }

    internal bool CancelReorder(LauncherReorderSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_activeReorder, session) || !session.TryCancel())
        {
            return false;
        }

        RestoreReorderSnapshot(session);
        EndReorder(session);
        StatusText = "已取消启动台排序。";
        return true;
    }

    internal async Task<bool> CommitReorderAsync(
        LauncherReorderSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfDisposed();
        if (!ReferenceEquals(_activeReorder, session) || !session.TryBeginCommit())
        {
            return false;
        }

        if (session.CurrentIndex == session.OriginalIndex)
        {
            EndReorder(session);
            return true;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        var gateAcquired = false;
        try
        {
            await _catalogGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateAcquired = true;
            IsCatalogBusy = true;
            var insertionSlot = session.OriginalIndex < session.CurrentIndex
                ? session.CurrentIndex + 1
                : session.CurrentIndex;
            if (!await _repository
                    .MoveToSlotAsync(session.Item.Item.Id, insertionSlot, operationToken)
                    .ConfigureAwait(true))
            {
                throw new InvalidOperationException("排序目标已变化，未能保存新顺序。");
            }

            var updatedUtc = DateTimeOffset.UtcNow;
            for (var index = 0; index < Items.Count; index++)
            {
                Items[index].UpdatePersistedOrder(index, updatedUtc);
            }

            StatusText = $"已将“{session.Item.DisplayName}”移动到第 {session.CurrentIndex + 1} 位。";
            EndReorder(session);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RestoreReorderSnapshot(session);
            EndReorder(session);
            RecordError("保存启动台顺序失败", exception);
            return false;
        }
        catch (OperationCanceledException)
        {
            RestoreReorderSnapshot(session);
            EndReorder(session);
            throw;
        }
        finally
        {
            IsCatalogBusy = false;
            if (gateAcquired)
            {
                _catalogGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_activeReorder is { } reorder && reorder.TryCancel())
        {
            RestoreReorderSnapshot(reorder);
            EndReorder(reorder);
        }
        OpenActionAsync = null;
        RevealActionAsync = null;
        _lifetime.Cancel();
    }

    [RelayCommand]
    private async Task OpenAsync(LauncherItemViewModel? item)
    {
        if (item is null || item.IsBusy || IsReordering || OpenActionAsync is null)
        {
            return;
        }

        await RunItemActionAsync(item, OpenActionAsync).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RevealAsync(LauncherItemViewModel? item)
    {
        if (item is null || item.IsBusy || IsReordering ||
            !item.CanRevealInExplorer || RevealActionAsync is null)
        {
            return;
        }

        await RunItemActionAsync(item, RevealActionAsync).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveAsync(LauncherItemViewModel? item)
    {
        if (item is null || item.IsBusy || IsReordering)
        {
            return;
        }

        await _catalogGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        try
        {
            item.IsBusy = true;
            if (await _repository
                    .RemoveAsync(item.Item.Id, _lifetime.Token)
                    .ConfigureAwait(true))
            {
                Items.Remove(item);
                _totalCount = Math.Max(0, _totalCount - 1);
                RebuildRows();
                StatusText = $"已从启动台移除“{item.DisplayName}”；原文件未受影响。";
            }
        }
        catch (Exception exception)
        {
            RecordError("移除启动项失败", exception);
        }
        finally
        {
            item.IsBusy = false;
            _catalogGate.Release();
        }
    }

    private async Task RunItemActionAsync(
        LauncherItemViewModel item,
        Func<LauncherItemViewModel, Task> action)
    {
        try
        {
            item.IsBusy = true;
            await action(item).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            RecordError($"无法处理“{item.DisplayName}”", exception);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void RebuildRows()
    {
        Rows.Clear();
        for (var index = 0; index < Items.Count; index += ColumnCount)
        {
            Rows.Add(new LauncherRowViewModel(
                Items.Skip(index).Take(ColumnCount).ToArray()));
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RestoreReorderSnapshot(LauncherReorderSession session)
    {
        Items.Clear();
        foreach (var item in session.OriginalOrder)
        {
            Items.Add(item);
        }

        RebuildRows();
    }

    private void EndReorder(LauncherReorderSession session)
    {
        session.Item.IsDragging = false;
        session.End();
        if (ReferenceEquals(_activeReorder, session))
        {
            _activeReorder = null;
            IsReordering = false;
        }
    }

    private void QueueMissingIcons()
    {
        foreach (var item in Items)
        {
            if (item.Icon is null)
            {
                QueueIcon(item);
            }
        }
    }

    private void QueueIcon(LauncherItemViewModel item)
    {
        _pendingIcons.Enqueue(item);
        if (_iconPumpRunning)
        {
            return;
        }

        _iconPumpRunning = true;
        _ = PumpIconsAsync();
    }

    private async Task PumpIconsAsync()
    {
        try
        {
            while (!_disposed && _pendingIcons.TryDequeue(out var item))
            {
                try
                {
                    var icon = await _shell
                        .GetIconAsync(item.Item, IconPixelSize, _lifetime.Token)
                        .ConfigureAwait(true);
                    if (icon is not null && Items.Contains(item))
                    {
                        item.Icon = DecodeIcon(icon.PngBytes);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    RecordError($"无法读取“{item.DisplayName}”的图标", exception);
                }
            }
        }
        finally
        {
            _iconPumpRunning = false;
            if (!_disposed && _pendingIcons.Count != 0)
            {
                _iconPumpRunning = true;
                _ = PumpIconsAsync();
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

    private static string CreateAddStatus(LauncherAddResult result, int failedCount)
    {
        var parts = new List<string>();
        if (result.Added.Count != 0)
        {
            parts.Add($"新增 {result.Added.Count} 项");
        }

        if (result.Existing.Count != 0)
        {
            parts.Add($"忽略 {result.Existing.Count} 项重复目标");
        }

        if (failedCount != 0)
        {
            parts.Add($"{failedCount} 项无法解析");
        }

        return parts.Count == 0 ? "启动台没有变化。" : string.Join("，", parts) + "。";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
