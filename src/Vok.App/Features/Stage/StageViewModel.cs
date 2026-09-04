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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
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
