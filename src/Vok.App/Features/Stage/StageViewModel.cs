using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public sealed partial class StageViewModel : ObservableObject, IDisposable
{
    internal const int PageSize = StageRollingWindow.DefaultSegmentSize;

    private readonly IStageRepository _repository;
    private readonly IStageFileService _fileService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StageRollingWindow _window = new();
    private StageReorderSession? _reorderSession;
    private long _nextReorderGeneration;
    private bool _initialized;
    private bool _disposed;
    private bool _isReordering;
    private bool _isCommitting;

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

    internal event Action<StageRollingWindowChange>? WindowProjectionChanging;

    internal Func<StageItemViewModel, Task>? OpenActionAsync { get; set; }

    internal Func<StageItemViewModel, Task>? RevealActionAsync { get; set; }

    internal Func<StageItemViewModel, Task>? CopyPathActionAsync { get; set; }

    internal Func<StageItemViewModel, CancellationToken, Task<StageRelinkSelection>>?
        RelinkTargetPickerAsync
    { get; set; }

    public IAsyncRelayCommand LoadNewerCommand { get; }

    public IAsyncRelayCommand LoadOlderCommand { get; }

    public bool IsReordering
    {
        get => _isReordering;
        private set
        {
            if (SetProperty(ref _isReordering, value))
            {
                NotifyInteractionStateChanged();
            }
        }
    }

    public bool IsCommitting
    {
        get => _isCommitting;
        private set
        {
            if (SetProperty(ref _isCommitting, value))
            {
                NotifyInteractionStateChanged();
            }
        }
    }

    public bool IsInteractionLocked => IsReordering || IsCommitting;

    public bool IsEmpty => _initialized && !IsLoading && !HasError && Items.Count == 0;

    public bool ShowErrorState => HasError && Items.Count == 0;

    public bool CanLoadNewer =>
        !_disposed && !IsLoading && !IsInteractionLocked &&
        _window.IsInitialized && !_window.RequiresReset && _window.HasNewer;

    public bool CanLoadOlder =>
        !_disposed && !IsLoading && !IsInteractionLocked &&
        _window.IsInitialized && !_window.RequiresReset && _window.HasOlder;

    public string PageSummaryText => $"已加载 {Items.Count} / {TotalCount}";

    internal int WindowMaximumItemCount => _window.MaximumItemCount;

    internal void EnsureViewportCapacity(int visibleItemCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleItemCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            visibleItemCapacity,
            int.MaxValue - PageSize);
        if (_disposed)
        {
            return;
        }

        var requiredItemCount = checked(visibleItemCapacity + PageSize);
        var requiredSegmentCount = checked(
            (requiredItemCount + PageSize - 1) / PageSize);
        _window.EnsureMaximumSegmentCount(requiredSegmentCount);
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
            await LoadNewestWindowLockedAsync(operationToken)
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
                .AddBatchAsync(targets, operationToken)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageAddBatchStatus.CapacityExceeded:
                    StatusText = $"空间不足，{targets.Length} 个有效目标整批未放入。";
                    return;

                case StageAddBatchStatus.SortOrderExhausted:
                    StatusText = "中转站顺序空间异常，未写入任何项目。";
                    return;

                case StageAddBatchStatus.Applied:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage result: {result.Status}.");
            }

            InvalidateWindowForReset();
            try
            {
                var page = await _repository
                    .LoadPageAsync(
                        anchor: null,
                        StagePageDirection.TowardOlder,
                        PageSize,
                        operationToken)
                    .ConfigureAwait(true);
                ApplyWindowReset(page);
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

    internal bool TryCancelReorderPreview(long generation = 0)
    {
        if (_reorderSession is not { State: StageReorderSessionState.Previewing } session ||
            (generation != 0 && session.Generation != generation))
        {
            return false;
        }

        RestoreReorderSnapshot(session);
        EndReorderSession(session);
        StatusText = "已取消本次移动。";
        return true;
    }

    internal long BeginReorder(StageItemViewModel? source)
    {
        if (source is null || source.IsBusy || _disposed || !_initialized ||
            IsLoading || IsInteractionLocked || Items.Count < 2)
        {
            return 0;
        }

        var sourceIndex = Items.IndexOf(source);
        if (sourceIndex < 0)
        {
            return 0;
        }

        var generation = checked(++_nextReorderGeneration);
        _reorderSession = new StageReorderSession(
            generation,
            source,
            Items.ToArray(),
            sourceIndex,
            _window.NewerBoundary,
            _window.OlderBoundary);
        source.IsDragging = true;
        IsReordering = true;
        StatusText = $"正在移动“{source.DisplayName}”…";
        return generation;
    }

    internal bool PreviewReorder(long generation, int targetIndex)
    {
        if (_reorderSession is not
            {
                State: StageReorderSessionState.Previewing,
            } session ||
            session.Generation != generation ||
            targetIndex < 0 || targetIndex >= Items.Count)
        {
            return false;
        }

        if (!Items.Contains(session.Source))
        {
            RestoreReorderSnapshot(session);
            EndReorderSession(session);
            return false;
        }

        session.CurrentIndex = targetIndex;
        return true;
    }

    internal async Task<bool> CommitReorderAsync(long generation)
    {
        if (_reorderSession is not { } session ||
            session.Generation != generation ||
            !session.TryBeginCommit())
        {
            return false;
        }

        session.Source.IsDragging = false;
        IsCommitting = true;
        if (session.CurrentIndex == session.OriginalIndex)
        {
            EndReorderSession(session);
            StatusText = "项目顺序没有变化。";
            return true;
        }

        StageReorderRequest request;
        try
        {
            var currentIndex = Items.IndexOf(session.Source);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Stage reorder source is no longer in the current page.");
            }

            Items.Move(currentIndex, session.CurrentIndex);
            request = CreateReorderRequest(session);
        }
        catch
        {
            RestoreReorderSnapshot(session);
            EndReorderSession(session);
            throw;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            SetLoading(true);
            HasError = false;
            StatusText = $"正在保存“{session.Source.DisplayName}”的新位置…";
            var result = await _repository
                .ReorderAsync(request, _lifetime.Token)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageReorderStatus.Applied:
                    await ApplyConfirmedReorderAsync(session, result, _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageReorderStatus.NoChange:
                    RestoreReorderSnapshot(session);
                    await ReloadFirstPageAfterRejectedReorderAsync(
                            $"“{session.Source.DisplayName}”已经位于该位置；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageReorderStatus.SortOrderExhausted:
                    RestoreReorderSnapshot(session);
                    StatusText = "当前区域没有足够的顺序空间，本次移动未保存。";
                    break;

                case StageReorderStatus.NotFound:
                    RestoreReorderSnapshot(session);
                    await ReloadFirstPageAfterRejectedReorderAsync(
                            "项目已不在中转站中，本次移动未保存；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageReorderStatus.RevisionConflict:
                case StageReorderStatus.NeighborConflict:
                    RestoreReorderSnapshot(session);
                    await ReloadFirstPageAfterRejectedReorderAsync(
                            "项目顺序已发生变化，本次移动未保存；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage reorder result: {result.Status}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RestoreReorderSnapshot(session);
            await ReloadFirstPageAfterUnknownReorderAsync(exception).ConfigureAwait(true);
        }
        finally
        {
            if (gateAcquired)
            {
                SetLoading(false);
                _operationGate.Release();
            }

            EndReorderSession(session);
        }

        return true;
    }

    [RelayCommand]
    private async Task RemoveAsync(StageItemViewModel? item)
    {
        if (item is null || item.IsBusy || _disposed || IsInteractionLocked)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item) || IsInteractionLocked)
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
        if (item is null || item.IsBusy || _disposed || IsInteractionLocked)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item) || IsInteractionLocked)
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

    [RelayCommand]
    private async Task RelinkAsync(StageItemViewModel? item)
    {
        if (item is null || item.IsBusy ||
            RelinkTargetPickerAsync is null || _disposed || IsInteractionLocked)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item) || RelinkTargetPickerAsync is null ||
                IsInteractionLocked)
            {
                return;
            }

            var original = item.Item;
            item.IsBusy = true;
            SetLoading(true);
            HasError = false;
            StatusText = $"请选择用于重新关联“{item.DisplayName}”的{item.TypeText}…";
            var selection = await RelinkTargetPickerAsync(item, _lifetime.Token)
                .ConfigureAwait(true);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (selection.Status is StageRelinkSelectionStatus.Cancelled)
            {
                StatusText = $"已取消重新关联“{original.ResolvedName}”。";
                return;
            }

            if (selection.Status is not StageRelinkSelectionStatus.Selected ||
                string.IsNullOrWhiteSpace(selection.Path))
            {
                throw new InvalidOperationException(
                    "The Stage relink picker returned an invalid selection.");
            }

            StatusText = $"正在验证“{original.ResolvedName}”的新目标…";
            var resolutions = await _fileService
                .ResolveAsync([selection.Path], _lifetime.Token)
                .ConfigureAwait(true);
            if (resolutions.Count != 1)
            {
                throw new InvalidOperationException(
                    "The Stage relink resolver did not return exactly one result.");
            }

            var resolution = resolutions[0];
            if (resolution.Target is not { } target)
            {
                StatusText = CreateRelinkResolutionFailureStatus(original, resolution);
                return;
            }

            if (target.Kind != original.Kind)
            {
                StatusText = $"所选目标不是{item.TypeText}，未更改“{original.ResolvedName}”。";
                return;
            }

            StatusText = $"正在重新关联“{original.ResolvedName}”…";
            var result = await _repository
                .RelinkAsync(
                    original.Id,
                    original.Revision,
                    target,
                    _lifetime.Token)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case StageRelinkStatus.Applied:
                    var authoritative = result.Item ??
                        throw new InvalidOperationException(
                            "An applied Stage relink did not return its authoritative item.");
                    var completedStatus =
                        $"已将“{original.ResolvedName}”重新关联到“{authoritative.ResolvedName}”。";
                    try
                    {
                        ApplyAuthoritativeItem(item, authoritative);
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

                case StageRelinkStatus.NoChange:
                    if (result.Item is { } unchanged && unchanged != item.Item)
                    {
                        ApplyAuthoritativeItem(item, unchanged);
                    }

                    StatusText = $"“{original.ResolvedName}”已经关联到该目标。";
                    break;

                case StageRelinkStatus.NotFound:
                    await ReloadAfterRejectedRelinkAsync(
                            "该项目已不在中转站中，未执行重新关联；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageRelinkStatus.RevisionConflict:
                    await ReloadAfterRejectedRelinkAsync(
                            "项目已发生变化，未执行重新关联；列表已刷新。",
                            _lifetime.Token)
                        .ConfigureAwait(true);
                    break;

                case StageRelinkStatus.KindMismatch:
                    StatusText = $"所选目标类型已变化，未更改“{original.ResolvedName}”。";
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage relink result: {result.Status}.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application lifetime cancellation owns shutdown cleanup.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HasError = true;
            StatusText = $"重新关联中转项目失败，结果待核对：{exception.Message}";
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
            action is null || _disposed || IsInteractionLocked)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            gateAcquired = true;
            if (!Items.Contains(item) || (!allowMissing && item.IsMissing) ||
                IsInteractionLocked)
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

    private StageReorderRequest CreateReorderRequest(StageReorderSession session)
    {
        var sourceIndex = Items.IndexOf(session.Source);
        if (sourceIndex < 0)
        {
            throw new InvalidOperationException(
                "The Stage reorder source is no longer in the current page.");
        }

        session.CurrentIndex = sourceIndex;
        var newerNeighbor = sourceIndex == 0
            ? session.NewerBoundary
            : ToOrderAnchor(Items[sourceIndex - 1]);
        var olderNeighbor = sourceIndex == Items.Count - 1
            ? session.OlderBoundary
            : ToOrderAnchor(Items[sourceIndex + 1]);
        return new StageReorderRequest(
            session.SourceVersion,
            newerNeighbor,
            olderNeighbor);
    }

    private async Task ApplyConfirmedReorderAsync(
        StageReorderSession session,
        StageReorderResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            ApplyReorderAuthority(session, result.ChangedItems);
            StatusText = $"已保存“{session.Source.DisplayName}”的新位置。";
        }
        catch (Exception applyException)
        {
            InvalidateWindowForReset();
            try
            {
                await ReloadNewestWindowCoreAsync(cancellationToken)
                    .ConfigureAwait(true);
                StatusText =
                    $"已保存“{session.Source.DisplayName}”的新位置，列表已重新加载。";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception refreshException)
            {
                HasError = true;
                StatusText =
                    $"顺序已保存，但列表刷新失败：{refreshException.Message}";
                System.Diagnostics.Trace.TraceWarning(
                    "Stage reorder committed but UI apply and refresh failed: {0}; {1}",
                    applyException.GetType().Name,
                    refreshException.GetType().Name);
            }
        }
    }

    private void ApplyReorderAuthority(
        StageReorderSession session,
        IReadOnlyList<StageItem> changedItems)
    {
        if (!changedItems.Any(item => item.Id == session.Source.Id))
        {
            throw new InvalidOperationException(
                "An applied Stage reorder did not return its source item.");
        }

        var changedById = changedItems.ToDictionary(static item => item.Id);
        foreach (var projection in Items)
        {
            if (changedById.TryGetValue(projection.Id, out var authoritative))
            {
                projection.ValidateReorderAuthority(authoritative);
            }
        }

        var projectedItems = Items
            .Select(item => changedById.GetValueOrDefault(item.Id) ?? item.Item)
            .ToArray();
        var newerBoundary = UpdateAnchor(_window.NewerBoundary, changedById);
        var olderBoundary = UpdateAnchor(_window.OlderBoundary, changedById);
        ValidateProjectedOrder(
            projectedItems,
            newerBoundary,
            olderBoundary);
        _window.ReconcileRetainedItems(
            projectedItems,
            newerBoundary,
            olderBoundary);

        foreach (var projection in Items)
        {
            if (changedById.TryGetValue(projection.Id, out var authoritative))
            {
                projection.ApplyReorderAuthority(authoritative);
            }
        }

        ValidateProjectionMatchesWindow();
        NotifyPageStateChanged();
    }

    private static void ValidateProjectedOrder(
        IReadOnlyList<StageItem> items,
        StageOrderAnchor? newerBoundary,
        StageOrderAnchor? olderBoundary)
    {
        var anchors = items.Select(ToOrderAnchor).ToArray();
        for (var index = 1; index < anchors.Length; index++)
        {
            if (anchors[index - 1].CompareTo(anchors[index]) <= 0)
            {
                throw new InvalidOperationException(
                    "An applied Stage reorder returned a page outside the requested order.");
            }
        }

        if (anchors.Length != 0 &&
            ((newerBoundary is { } newer && newer.CompareTo(anchors[0]) <= 0) ||
             (olderBoundary is { } older && anchors[^1].CompareTo(older) <= 0)))
        {
            throw new InvalidOperationException(
                "An applied Stage reorder crossed a page boundary.");
        }
    }

    private async Task ReloadFirstPageAfterRejectedReorderAsync(
        string successStatus,
        CancellationToken cancellationToken)
    {
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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
            StatusText = $"本次移动未保存，且列表刷新失败：{exception.Message}";
        }
    }

    private async Task ReloadFirstPageAfterUnknownReorderAsync(Exception exception)
    {
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(_lifetime.Token)
                .ConfigureAwait(true);
            HasError = true;
            StatusText = $"排序结果待核对，列表已重新加载：{exception.Message}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception refreshException)
        {
            HasError = true;
            StatusText = $"排序结果待核对，且列表刷新失败：{refreshException.Message}";
        }
    }

    private void RestoreReorderSnapshot(StageReorderSession session)
    {
        if (Items.Count != session.OriginalItems.Count)
        {
            throw new InvalidOperationException(
                "The Stage page changed while a same-page reorder was active.");
        }

        for (var targetIndex = 0; targetIndex < session.OriginalItems.Count; targetIndex++)
        {
            var expected = session.OriginalItems[targetIndex];
            var currentIndex = Items.IndexOf(expected);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Stage page replaced an item while a same-page reorder was active.");
            }

            if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }

        session.CurrentIndex = session.OriginalIndex;
    }

    private void EndReorderSession(StageReorderSession session)
    {
        if (!ReferenceEquals(_reorderSession, session))
        {
            return;
        }

        _ = session.TryEnd();
        session.Source.IsDragging = false;
        _reorderSession = null;
        IsCommitting = false;
        IsReordering = false;
    }

    private static StageOrderAnchor ToOrderAnchor(StageItemViewModel item) =>
        ToOrderAnchor(item.Item);

    private static StageOrderAnchor ToOrderAnchor(StageItem item) =>
        new(item.Id, item.Revision, item.SortOrder);

    private static StageOrderAnchor? UpdateAnchor(
        StageOrderAnchor? anchor,
        IReadOnlyDictionary<Guid, StageItem> changedById) =>
        anchor is { } current && changedById.TryGetValue(current.Id, out var changed)
            ? ToOrderAnchor(changed)
            : anchor;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_reorderSession is { State: StageReorderSessionState.Previewing } session)
        {
            RestoreReorderSnapshot(session);
            EndReorderSession(session);
        }
        else if (_reorderSession is { } activeSession)
        {
            EndReorderSession(activeSession);
        }

        _disposed = true;
        _lifetime.Cancel();
        OpenActionAsync = null;
        RevealActionAsync = null;
        CopyPathActionAsync = null;
        RelinkTargetPickerAsync = null;
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
            if (!_initialized || IsInteractionLocked)
            {
                return;
            }

            var direction = loadOlder
                ? StagePageDirection.TowardOlder
                : StagePageDirection.TowardNewer;
            await LoadWindowEdgeLockedAsync(direction, operationToken)
                .ConfigureAwait(true);
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

    private async Task LoadNewestWindowLockedAsync(
        CancellationToken cancellationToken)
    {
        SetLoading(true);
        HasError = false;
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task LoadWindowEdgeLockedAsync(
        StagePageDirection direction,
        CancellationToken cancellationToken)
    {
        var request = _window.CreateLoadRequest(direction);
        if (request is null)
        {
            return;
        }

        SetLoading(true);
        HasError = false;
        try
        {
            var page = await _repository
                .LoadPageAsync(
                    request.Anchor,
                    direction,
                    PageSize,
                    cancellationToken)
                .ConfigureAwait(true);
            var result = _window.Apply(request, page);
            switch (result.Status)
            {
                case StageRollingWindowApplyStatus.Applied:
                    ApplyWindowChange(result.Change ?? throw new InvalidOperationException(
                        "An applied Stage rolling-window load did not return its delta."));
                    SetLoadedStatus();
                    break;

                case StageRollingWindowApplyStatus.Stale:
                    break;

                case StageRollingWindowApplyStatus.ResetRequired:
                    await ReloadNewestWindowCoreAsync(cancellationToken)
                        .ConfigureAwait(true);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Stage rolling-window result: {result.Status}.");
            }
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task ReloadNewestWindowCoreAsync(
        CancellationToken cancellationToken)
    {
        var page = await _repository
            .LoadPageAsync(
                anchor: null,
                StagePageDirection.TowardOlder,
                PageSize,
                cancellationToken)
            .ConfigureAwait(true);
        ApplyWindowReset(page);
        SetLoadedStatus();
    }

    private async Task ApplyConfirmedRemovalAsync(
        StageItemViewModel item,
        CancellationToken cancellationToken)
    {
        var displayName = item.DisplayName;
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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

    private async Task ReloadAfterRejectedRelinkAsync(
        string successStatus,
        CancellationToken cancellationToken)
    {
        InvalidateWindowForReset();
        try
        {
            await ReloadNewestWindowCoreAsync(cancellationToken)
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
            StatusText = $"未执行重新关联，且列表刷新失败：{exception.Message}";
        }
    }

    private void ApplyAuthoritativeItem(
        StageItemViewModel projection,
        StageItem authoritativeItem)
    {
        if (!Items.Contains(projection))
        {
            throw new InvalidOperationException(
                "An authoritative Stage item is no longer retained by the window.");
        }

        if (authoritativeItem.Id != projection.Id)
        {
            throw new InvalidOperationException(
                "An authoritative Stage item update changed its occurrence identity.");
        }

        var changedById = new Dictionary<Guid, StageItem>
        {
            [authoritativeItem.Id] = authoritativeItem,
        };
        var projectedItems = Items
            .Select(item => changedById.GetValueOrDefault(item.Id) ?? item.Item)
            .ToArray();
        _window.ReconcileRetainedItems(
            projectedItems,
            UpdateAnchor(_window.NewerBoundary, changedById),
            UpdateAnchor(_window.OlderBoundary, changedById));
        projection.Update(authoritativeItem);
        ValidateProjectionMatchesWindow();
        ItemAuthorityChanged?.Invoke(projection);
        NotifyPageStateChanged();
    }

    private void ApplyWindowReset(StagePage page)
    {
        var change = _window.Reset(page);
        WindowProjectionChanging?.Invoke(change);
        ReplaceProjectionFromWindow();
        ValidateProjectionMatchesWindow();
        TotalCount = _window.TotalCount;
        NotifyPageStateChanged();
    }

    private void ApplyWindowChange(StageRollingWindowChange change)
    {
        if (change.IsReset || change.AddedAt is null)
        {
            throw new InvalidOperationException(
                "An edge Stage rolling-window change must describe its added edge.");
        }

        ValidateProjectionBeforeWindowChange(change);
        WindowProjectionChanging?.Invoke(change);
        try
        {
            if (change.EvictedFrom is StageRollingWindowEdge.Newer)
            {
                for (var index = 0; index < change.EvictedItems.Count; index++)
                {
                    Items.RemoveAt(0);
                }
            }
            else if (change.EvictedFrom is StageRollingWindowEdge.Older)
            {
                for (var index = 0; index < change.EvictedItems.Count; index++)
                {
                    Items.RemoveAt(Items.Count - 1);
                }
            }

            if (change.AddedAt is StageRollingWindowEdge.Newer)
            {
                for (var index = change.AddedItems.Count - 1; index >= 0; index--)
                {
                    Items.Insert(0, new StageItemViewModel(change.AddedItems[index]));
                }
            }
            else
            {
                foreach (var item in change.AddedItems)
                {
                    Items.Add(new StageItemViewModel(item));
                }
            }

            ValidateProjectionMatchesWindow();
        }
        catch (Exception applyException)
        {
            try
            {
                ReplaceProjectionFromWindow();
            }
            catch (Exception recoveryException)
            {
                throw new AggregateException(
                    "A Stage rolling-window delta and its projection recovery both failed.",
                    applyException,
                    recoveryException);
            }

            throw new InvalidOperationException(
                "A Stage rolling-window delta could not be applied to its projection.",
                applyException);
        }

        TotalCount = _window.TotalCount;
        NotifyPageStateChanged();
    }

    private void ValidateProjectionBeforeWindowChange(
        StageRollingWindowChange change)
    {
        var expectedOldIds = new List<Guid>(
            checked(_window.Items.Count - change.AddedItems.Count +
                change.EvictedItems.Count));
        if (change.AddedAt is StageRollingWindowEdge.Newer)
        {
            expectedOldIds.AddRange(_window.Items
                .Skip(change.AddedItems.Count)
                .Select(static item => item.Id));
            expectedOldIds.AddRange(change.EvictedItems.Select(static item => item.Id));
        }
        else
        {
            expectedOldIds.AddRange(change.EvictedItems.Select(static item => item.Id));
            expectedOldIds.AddRange(_window.Items
                .Take(_window.Items.Count - change.AddedItems.Count)
                .Select(static item => item.Id));
        }

        if (!expectedOldIds.SequenceEqual(Items.Select(static item => item.Id)))
        {
            throw new InvalidOperationException(
                "The Stage item projection no longer matches the rolling-window delta base.");
        }
    }

    private void ValidateProjectionMatchesWindow()
    {
        if (!Items.Select(static item => item.Item).SequenceEqual(_window.Items))
        {
            throw new InvalidOperationException(
                "The Stage item projection does not match the rolling window.");
        }
    }

    private void ReplaceProjectionFromWindow()
    {
        Items.Clear();
        foreach (var item in _window.Items)
        {
            Items.Add(new StageItemViewModel(item));
        }
    }

    private void InvalidateWindowForReset()
    {
        if (_window.IsInitialized)
        {
            _window.Invalidate();
            NotifyPageStateChanged();
        }
    }

    private void SetLoadedStatus()
    {
        StatusText = _window.TotalCount == 0
            ? "中转站为空。"
            : $"中转站共有 {_window.TotalCount} 个项目；按住卡片可直接排序。";
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

    private void NotifyInteractionStateChanged()
    {
        OnPropertyChanged(nameof(IsInteractionLocked));
        NotifyPageStateChanged();
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

    private static string CreateRelinkResolutionFailureStatus(
        StageItem original,
        StageTargetResolution resolution)
    {
        var reason = string.IsNullOrWhiteSpace(resolution.Error)
            ? resolution.Failure?.ToString() ?? "未知原因"
            : resolution.Error;
        return $"无法重新关联“{original.ResolvedName}”：{reason}";
    }

    private static string CreateAppliedStatus(
        StageAddBatchResult result,
        int invalidCount)
    {
        var parts = new List<string>();
        if (result.AddedCount != 0)
        {
            parts.Add($"新增 {result.AddedCount} 项");
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
