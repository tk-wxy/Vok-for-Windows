using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public sealed partial class StageViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = 64;

    private readonly IStageRepository _repository;
    private readonly IStageFileService _fileService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<StagePageCursor?> _pageStarts = [null];
    private StagePageCursor? _nextCursor;
    private int _pageIndex;
    private bool _initialized;
    private bool _disposed;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "正在加载中转站…";

    public StageViewModel(
        IStageRepository repository,
        IStageFileService fileService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        LoadNewerCommand = new AsyncRelayCommand(
            (Func<Task>)LoadNewerPageAsync,
            () => CanLoadNewer);
        LoadOlderCommand = new AsyncRelayCommand(
            (Func<Task>)LoadOlderPageAsync,
            () => CanLoadOlder);
    }

    public ObservableCollection<StageItemViewModel> Items { get; } = [];

    internal event Action<StageItemViewModel>? ItemAuthorityChanged;

    internal Func<StageItemViewModel, Task>? OpenActionAsync { get; set; }

    internal Func<StageItemViewModel, Task>? RevealActionAsync { get; set; }

    internal Func<StageItemViewModel, Task>? CopyPathActionAsync { get; set; }

    public IAsyncRelayCommand LoadNewerCommand { get; }

    public IAsyncRelayCommand LoadOlderCommand { get; }

    public bool IsEmpty => _initialized && !IsLoading && !HasError && Items.Count == 0;

    public bool ShowErrorState => HasError && Items.Count == 0;

    public bool CanLoadNewer => !_disposed && !IsLoading && _pageIndex > 0;

    public bool CanLoadOlder => !_disposed && !IsLoading && _nextCursor is not null;

    public string PageSummaryText
    {
        get
        {
            if (Items.Count == 0)
            {
                return $"0 / {TotalCount}";
            }

            var first = checked((long)_pageIndex * PageSize + 1);
            var last = checked(first + Items.Count - 1);
            return $"{first}–{last} / {TotalCount}";
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateAcquired = true;
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await LoadPageLockedAsync(
                    after: null,
                    pageIndex: 0,
                    resetHistory: true,
                    operationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordLoadFailure("中转站加载失败", exception);
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }
        }
    }

    public Task LoadNewerPageAsync() =>
        NavigateAsync(loadOlder: false, CancellationToken.None);

    public Task LoadOlderPageAsync() =>
        NavigateAsync(loadOlder: true, CancellationToken.None);

    internal Task LoadNewerPageAsync(CancellationToken cancellationToken) =>
        NavigateAsync(loadOlder: false, cancellationToken);

    internal Task LoadOlderPageAsync(CancellationToken cancellationToken) =>
        NavigateAsync(loadOlder: true, cancellationToken);

    internal async Task<StageFileAvailability> InspectAvailabilityAsync(
        StageItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        return await _fileService
            .InspectAsync(item.Item, linkedCancellation.Token)
            .ConfigureAwait(true);
    }

    public async Task AddDroppedPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ThrowIfDisposed();
        var snapshot = paths.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateAcquired = true;
            SetLoading(true);
            HasError = false;
            StatusText = $"正在处理 {snapshot.Length} 个目标…";

            var resolutions = await _fileService
                .ResolveAsync(snapshot, operationToken)
                .ConfigureAwait(true);
            var targets = resolutions
                .Where(static resolution => resolution.Target is not null)
                .Select(static resolution => resolution.Target!)
                .ToArray();
            var invalid = resolutions
                .Where(static resolution => !resolution.Succeeded)
                .ToArray();
            if (targets.Length == 0)
            {
                StatusText = CreateAllInvalidStatus(invalid, snapshot.Length);
                return;
            }

            var result = await _repository
                .AddOrMoveAsync(targets, operationToken)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageAddOrMoveStatus.CapacityExceeded:
                    StatusText = $"空间不足，{targets.Length} 个有效目标整批未放入。";
                    return;

                case StageAddOrMoveStatus.SortOrderExhausted:
                    StatusText = "中转站顺序空间异常，未写入任何项目。";
                    return;

                case StageAddOrMoveStatus.Applied:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage result: {result.Status}.");
            }

            try
            {
                var page = await _repository
                    .LoadPageAsync(after: null, PageSize, operationToken)
                    .ConfigureAwait(true);
                ApplyPage(page, pageIndex: 0, start: null, resetHistory: true);
                StatusText = CreateAppliedStatus(result, invalid.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                HasError = true;
                StatusText = $"项目已保存，但列表刷新失败：{exception.Message}";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasError = true;
            StatusText = $"处理中转项目失败，结果待核对：{exception.Message}";
        }
        finally
        {
            if (gateAcquired)
            {
                SetLoading(false);
                _operationGate.Release();
            }
        }
    }

    public void RecordDispatchFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_disposed)
        {
            return;
        }

        HasError = true;
        StatusText = $"处理中转项目失败，结果待核对：{exception.Message}";
        NotifyPageStateChanged();
    }

    [RelayCommand]
    private async Task RemoveAsync(StageItemViewModel? item)
    {
        if (item is null || item.IsBusy || _disposed)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item))
            {
                return;
            }

            item.IsBusy = true;
            SetLoading(true);
            HasError = false;
            StatusText = $"正在移除“{item.DisplayName}”…";
            var result = await _repository
                .RemoveAsync(item.Id, item.Revision, _lifetime.Token)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageRemoveStatus.Removed:
                    await ApplyConfirmedRemovalAsync(item, _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageRemoveStatus.NotFound:
                    await ReloadAfterRejectedRemovalAsync(
                            "该项目已不在中转站中，列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageRemoveStatus.RevisionConflict:
                    await ReloadAfterRejectedRemovalAsync(
                            "项目已发生变化，未执行移除；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage removal result: {result.Status}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasError = true;
            StatusText = $"移除中转项目失败，结果待核对：{exception.Message}";
        }
        finally
        {
            item.IsBusy = false;
            if (gateAcquired)
            {
                SetLoading(false);
                _operationGate.Release();
            }
        }
    }

    [RelayCommand]
    private async Task TogglePinnedAsync(StageItemViewModel? item)
    {
        if (item is null || item.IsBusy || _disposed)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item))
            {
                return;
            }

            var isPinned = !item.IsPinned;
            item.IsBusy = true;
            SetLoading(true);
            HasError = false;
            StatusText = isPinned
                ? $"正在固定“{item.DisplayName}”…"
                : $"正在取消固定“{item.DisplayName}”…";
            var result = await _repository
                .SetPinnedAsync(
                    item.Id,
                    item.Revision,
                    isPinned,
                    _lifetime.Token)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageItemMutationStatus.Applied:
                    var completedStatus = isPinned
                        ? $"已固定“{item.DisplayName}”。"
                        : $"已取消固定“{item.DisplayName}”。";
                    try
                    {
                        ApplyAuthoritativeItem(
                            item,
                            result.Item ?? throw new InvalidOperationException(
                                "An applied Stage mutation did not return its authoritative item."));
                        StatusText = completedStatus;
                    }
                    catch (Exception exception)
                    {
                        await ReloadAfterConfirmedItemMutationAsync(
                                completedStatus,
                                exception,
                                _lifetime.Token)
                            .ConfigureAwait(true);
                    }

                    break;

                case StageItemMutationStatus.NoChange:
                    if (result.Item is { } unchanged && unchanged != item.Item)
                    {
                        ApplyAuthoritativeItem(item, unchanged);
                    }

                    StatusText = isPinned
                        ? $"“{item.DisplayName}”已经固定。"
                        : $"“{item.DisplayName}”已经取消固定。";
                    break;

                case StageItemMutationStatus.NotFound:
                    await ReloadAfterRejectedItemMutationAsync(
                            "该项目已不在中转站中，未更改固定状态；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageItemMutationStatus.RevisionConflict:
                    await ReloadAfterRejectedItemMutationAsync(
                            "项目已发生变化，未更改固定状态；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage item mutation result: {result.Status}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasError = true;
            StatusText = $"更新固定状态失败，结果待核对：{exception.Message}";
        }
        finally
        {
            item.IsBusy = false;
            if (gateAcquired)
            {
                SetLoading(false);
                _operationGate.Release();
            }
        }
    }

    [RelayCommand]
    private Task OpenAsync(StageItemViewModel? item) =>
        RunFileActionAsync(
            item,
            OpenActionAsync,
            "正在打开",
            "已将目标交给 Windows 打开",
            "打开中转项目失败");

    [RelayCommand]
    private Task RevealAsync(StageItemViewModel? item) =>
        RunFileActionAsync(
            item,
            RevealActionAsync,
            "正在定位",
            "已在文件资源管理器中定位",
            "定位中转项目失败");

    [RelayCommand]
    private Task CopyPathAsync(StageItemViewModel? item) =>
        RunFileActionAsync(
            item,
            CopyPathActionAsync,
            "正在复制路径",
            "已复制原路径",
            "复制原路径失败",
            allowMissing: true);

    private async Task RunFileActionAsync(
        StageItemViewModel? item,
        Func<StageItemViewModel, Task>? action,
        string progressText,
        string completedText,
        string failureText,
        bool allowMissing = false)
    {
        if (item is null || item.IsBusy ||
            (!allowMissing && item.IsMissing) ||
            action is null || _disposed)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item) || (!allowMissing && item.IsMissing))
            {
                return;
            }

            item.IsBusy = true;
            SetLoading(true);
            HasError = false;
            StatusText = $"{progressText}“{item.DisplayName}”…";
            await action(item).ConfigureAwait(true);
            StatusText = $"{completedText}“{item.DisplayName}”。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasError = true;
            StatusText = $"{failureText}：{exception.Message}";
        }
        finally
        {
            item.IsBusy = false;
            if (gateAcquired)
            {
                SetLoading(false);
                _operationGate.Release();
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
        _lifetime.Cancel();
        OpenActionAsync = null;
        RevealActionAsync = null;
        CopyPathActionAsync = null;
        NotifyPageStateChanged();
    }

    private async Task NavigateAsync(
        bool loadOlder,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var operationToken = linkedCancellation.Token;
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateAcquired = true;
            if (!_initialized)
            {
                return;
            }

            if (loadOlder)
            {
                if (_nextCursor is not { } next)
                {
                    return;
                }

                var nextIndex = checked(_pageIndex + 1);
                var start = nextIndex < _pageStarts.Count
                    ? _pageStarts[nextIndex]
                    : next;
                await LoadPageLockedAsync(start, nextIndex, resetHistory: false, operationToken)
                    .ConfigureAwait(true);
            }
            else
            {
                if (_pageIndex == 0)
                {
                    return;
                }

                var previousIndex = _pageIndex - 1;
                await LoadPageLockedAsync(
                        _pageStarts[previousIndex],
                        previousIndex,
                        resetHistory: false,
                        operationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordLoadFailure("中转站翻页失败", exception);
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }
        }
    }

    private async Task LoadPageLockedAsync(
        StagePageCursor? after,
        int pageIndex,
        bool resetHistory,
        CancellationToken cancellationToken)
    {
        SetLoading(true);
        HasError = false;
        try
        {
            var page = await _repository
                .LoadPageAsync(after, PageSize, cancellationToken)
                .ConfigureAwait(true);
            ApplyPage(page, pageIndex, after, resetHistory);
            StatusText = page.TotalCount == 0
                ? "中转站为空。"
                : $"中转站共有 {page.TotalCount} 个项目。";
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task ApplyConfirmedRemovalAsync(
        StageItemViewModel item,
        CancellationToken cancellationToken)
    {
        var displayName = item.DisplayName;
        _ = Items.Remove(item);
        TotalCount = Math.Max(0, TotalCount - 1);
        NotifyPageStateChanged();

        var targetPageIndex = _pageIndex;
        if (Items.Count == 0 && TotalCount > 0 && targetPageIndex > 0)
        {
            targetPageIndex--;
        }

        var start = targetPageIndex == 0
            ? null
            : _pageStarts[targetPageIndex];
        TruncatePageHistoryAfter(targetPageIndex);
        try
        {
            await LoadPageLockedAsync(
                    start,
                    targetPageIndex,
                    resetHistory: targetPageIndex == 0,
                    cancellationToken)
                .ConfigureAwait(true);
            StatusText = $"已从中转站移除“{displayName}”；原文件未受影响。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"已移除“{displayName}”，但列表刷新失败：{exception.Message}";
        }
    }

    private async Task ReloadAfterRejectedRemovalAsync(
        string successStatus,
        CancellationToken cancellationToken)
    {
        var start = _pageIndex == 0 ? null : _pageStarts[_pageIndex];
        TruncatePageHistoryAfter(_pageIndex);
        try
        {
            await LoadPageLockedAsync(
                    start,
                    _pageIndex,
                    resetHistory: _pageIndex == 0,
                    cancellationToken)
                .ConfigureAwait(true);
            StatusText = successStatus;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"未执行移除，且列表刷新失败：{exception.Message}";
        }
    }

    private async Task ReloadAfterRejectedItemMutationAsync(
        string successStatus,
        CancellationToken cancellationToken)
    {
        var start = _pageIndex == 0 ? null : _pageStarts[_pageIndex];
        TruncatePageHistoryAfter(_pageIndex);
        try
        {
            await LoadPageLockedAsync(
                    start,
                    _pageIndex,
                    resetHistory: _pageIndex == 0,
                    cancellationToken)
                .ConfigureAwait(true);
            StatusText = successStatus;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusText = $"未更改固定状态，且列表刷新失败：{exception.Message}";
        }
    }

    private async Task ReloadAfterConfirmedItemMutationAsync(
        string committedStatus,
        Exception applyException,
        CancellationToken cancellationToken)
    {
        var start = _pageIndex == 0 ? null : _pageStarts[_pageIndex];
        TruncatePageHistoryAfter(_pageIndex);
        try
        {
            await LoadPageLockedAsync(
                    start,
                    _pageIndex,
                    resetHistory: _pageIndex == 0,
                    cancellationToken)
                .ConfigureAwait(true);
            StatusText = $"{committedStatus} 列表已重新加载。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception refreshException)
        {
            HasError = true;
            StatusText = $"{committedStatus} 但列表刷新失败：{refreshException.Message}";
            System.Diagnostics.Trace.TraceWarning(
                "Stage item mutation committed but UI apply and refresh failed: {0}; {1}",
                applyException.GetType().Name,
                refreshException.GetType().Name);
        }
    }

    private void ApplyAuthoritativeItem(
        StageItemViewModel projection,
        StageItem authoritativeItem)
    {
        projection.Update(authoritativeItem);
        ItemAuthorityChanged?.Invoke(projection);
    }

    private void TruncatePageHistoryAfter(int pageIndex)
    {
        while (_pageStarts.Count > pageIndex + 1)
        {
            _pageStarts.RemoveAt(_pageStarts.Count - 1);
        }
    }

    private void ApplyPage(
        StagePage page,
        int pageIndex,
        StagePageCursor? start,
        bool resetHistory)
    {
        if (page.Items.Count > PageSize)
        {
            throw new InvalidOperationException(
                $"Stage repository returned more than {PageSize} items.");
        }

        if (resetHistory)
        {
            _pageStarts.Clear();
            _pageStarts.Add(null);
        }
        else if (pageIndex == _pageStarts.Count)
        {
            _pageStarts.Add(start);
        }
        else if (pageIndex > _pageStarts.Count)
        {
            throw new InvalidOperationException("Stage page cursor history is discontinuous.");
        }

        Items.Clear();
        foreach (var item in page.Items)
        {
            Items.Add(new StageItemViewModel(item));
        }

        _pageIndex = pageIndex;
        _nextCursor = page.NextCursor;
        TotalCount = page.TotalCount;
        NotifyPageStateChanged();
    }

    private void RecordLoadFailure(string operation, Exception exception)
    {
        HasError = true;
        StatusText = $"{operation}：{exception.Message}";
        SetLoading(false);
        NotifyPageStateChanged();
    }

    private void SetLoading(bool value)
    {
        IsLoading = value;
        NotifyPageStateChanged();
    }

    private void NotifyPageStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(CanLoadNewer));
        OnPropertyChanged(nameof(CanLoadOlder));
        OnPropertyChanged(nameof(PageSummaryText));
        LoadNewerCommand.NotifyCanExecuteChanged();
        LoadOlderCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalCountChanged(long value) => NotifyPageStateChanged();

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowErrorState));
    }

    private static string CreateAllInvalidStatus(
        IReadOnlyList<StageTargetResolution> invalid,
        int requestedCount)
    {
        var count = invalid.Count == 0 ? requestedCount : invalid.Count;
        var reason = invalid.FirstOrDefault()?.Failure switch
        {
            StageTargetFailureKind.Empty => "目标为空",
            StageTargetFailureKind.NotFullyQualified => "路径不是完整路径",
            StageTargetFailureKind.Missing => "目标不存在",
            StageTargetFailureKind.AccessDenied => "没有访问权限",
            StageTargetFailureKind.InvalidPath => "路径无效",
            StageTargetFailureKind.Unsupported => "目标类型不受支持",
            StageTargetFailureKind.Unavailable => "目标暂不可用",
            _ => "没有有效的文件系统目标",
        };
        return $"未放入任何项目；{count} 项无效（{reason}）。";
    }

    private static string CreateAppliedStatus(
        StageAddOrMoveResult result,
        int invalidCount)
    {
        var parts = new List<string>();
        if (result.AddedCount != 0)
        {
            parts.Add($"新增 {result.AddedCount} 项");
        }

        if (result.MovedCount != 0)
        {
            parts.Add($"回顶 {result.MovedCount} 项");
        }

        if (invalidCount != 0)
        {
            parts.Add($"{invalidCount} 项无效");
        }

        return parts.Count == 0
            ? "中转站没有变化。"
            : string.Join("，", parts) + "。";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
