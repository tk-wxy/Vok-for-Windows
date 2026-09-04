using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Vok.Hosting;

internal sealed class WorkspaceFileDropRouter : IDisposable
{
    private readonly IReadOnlyDictionary<WorkspaceFileDropTargetKind, IWorkspaceFileDropTarget>
        _targets;
    private readonly Action<long> _dragStarted;
    private readonly Action<long, bool> _dragEnded;
    private readonly Func<bool> _isOwnerVisible;
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextDragId;
    private long _activeDragId;
    private WorkspaceFileDropTargetKind _currentTarget;
    private bool _disposed;

    public WorkspaceFileDropRouter(
        IEnumerable<IWorkspaceFileDropTarget> targets,
        Action<long> dragStarted,
        Action<long, bool> dragEnded,
        Func<bool> isOwnerVisible)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _dragStarted = dragStarted ?? throw new ArgumentNullException(nameof(dragStarted));
        _dragEnded = dragEnded ?? throw new ArgumentNullException(nameof(dragEnded));
        _isOwnerVisible = isOwnerVisible ?? throw new ArgumentNullException(nameof(isOwnerVisible));
        _targets = targets.ToDictionary(static target => target.Kind);
        if (_targets.ContainsKey(WorkspaceFileDropTargetKind.None))
        {
            throw new ArgumentException("None cannot be registered as a drop target.", nameof(targets));
        }
    }

    public bool IsTracking => _activeDragId != 0;

    public WorkspaceFileDropTargetKind CurrentTarget => _currentTarget;

    public bool Begin(IDataObject data, WorkspaceFileDropTargetKind target)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfDisposed();

        foreach (var adapter in _targets.Values)
        {
            adapter.CancelInteractions();
        }

        var replacedActiveDrag = IsTracking;
        Cancel();
        if (_disposed || (replacedActiveDrag && !_isOwnerVisible()))
        {
            return false;
        }

        if (!HasFileDrop(data))
        {
            return false;
        }

        var dragId = Interlocked.Increment(ref _nextDragId);
        _activeDragId = dragId;
        _currentTarget = NormalizeTarget(target);
        _dragStarted(dragId);
        if (_activeDragId != dragId)
        {
            return false;
        }

        UpdateVisuals(sessionActive: true, _currentTarget);
        return true;
    }

    public bool Update(WorkspaceFileDropTargetKind target)
    {
        ThrowIfDisposed();
        if (!IsTracking)
        {
            return false;
        }

        _currentTarget = NormalizeTarget(target);
        UpdateVisuals(sessionActive: true, _currentTarget);
        return _currentTarget is not WorkspaceFileDropTargetKind.None;
    }

    public WorkspaceExternalFileDropResult Complete(
        IDataObject data,
        WorkspaceFileDropTargetKind target)
    {
        ArgumentNullException.ThrowIfNull(data);
        ThrowIfDisposed();
        if (!IsTracking)
        {
            return WorkspaceExternalFileDropResult.Rejected;
        }

        var frozenTarget = NormalizeTarget(target);
        var dragId = Claim();
        if (dragId == 0 || frozenTarget is WorkspaceFileDropTargetKind.None)
        {
            if (dragId != 0)
            {
                _dragEnded(dragId, false);
            }

            return WorkspaceExternalFileDropResult.Rejected;
        }

        if (!TrySnapshotFileDrop(data, out var paths))
        {
            _dragEnded(dragId, false);
            return WorkspaceExternalFileDropResult.Rejected;
        }

        _dragEnded(dragId, true);
        var adapter = _targets[frozenTarget];
        _ = ObserveDispatchAsync(adapter, paths);
        return new WorkspaceExternalFileDropResult(true, paths);
    }

    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        var dragId = Claim();
        if (dragId != 0)
        {
            _dragEnded(dragId, false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cancel();
        _disposed = true;
        _lifetime.Cancel();
    }

    private async Task ObserveDispatchAsync(
        IWorkspaceFileDropTarget target,
        IReadOnlyList<string> paths)
    {
        try
        {
            await target.ReceiveAsync(paths, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application exit cancels in-flight dispatch without surfacing an error.
        }
        catch (Exception exception)
        {
            try
            {
                target.ReportDispatchFailure(exception);
            }
            catch (Exception reportingException)
            {
                Trace.TraceError(
                    "Unable to report {0} drop dispatch failure: {1}; original failure: {2}",
                    target.Kind,
                    reportingException,
                    exception);
            }
        }
    }

    private WorkspaceFileDropTargetKind NormalizeTarget(WorkspaceFileDropTargetKind target) =>
        target is not WorkspaceFileDropTargetKind.None &&
        _targets.TryGetValue(target, out var adapter) &&
        adapter.IsAvailable
            ? target
            : WorkspaceFileDropTargetKind.None;

    private long Claim()
    {
        var dragId = _activeDragId;
        if (dragId == 0)
        {
            return 0;
        }

        _activeDragId = 0;
        _currentTarget = WorkspaceFileDropTargetKind.None;
        UpdateVisuals(sessionActive: false, WorkspaceFileDropTargetKind.None);
        return dragId;
    }

    private void UpdateVisuals(
        bool sessionActive,
        WorkspaceFileDropTargetKind targeted)
    {
        foreach (var adapter in _targets.Values)
        {
            adapter.SetExternalDragState(sessionActive, targeted: false);
        }

        if (sessionActive && _targets.TryGetValue(targeted, out var target))
        {
            target.SetExternalDragState(sessionActive: true, targeted: true);
        }
    }

    private static bool HasFileDrop(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySnapshotFileDrop(
        IDataObject data,
        out IReadOnlyList<string> paths)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] values &&
                values.Length != 0)
            {
                paths = values.ToArray();
                return true;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // A foreign OLE source can withdraw a format while the drag is terminating.
        }

        paths = [];
        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record WorkspaceExternalFileDropResult(
    bool Accepted,
    IReadOnlyList<string> Paths)
{
    public static WorkspaceExternalFileDropResult Rejected { get; } = new(false, []);
}
