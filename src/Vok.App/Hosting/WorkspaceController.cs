using System.Diagnostics;
using System.Windows;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Presentation;

namespace Vok.Hosting;

public sealed class WorkspaceController : IDisposable
{
    private readonly IWorkspaceWindow _window;
    private readonly IWorkspaceFrameSource _frameSource;
    private readonly MainWindowViewModel _viewModel;
    private readonly WorkspaceSessionStateMachine _stateMachine;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly IForegroundWindowService _foregroundWindowService;
    private readonly IWindowPlacementService _windowPlacementService;
    private readonly IPerformanceSampler _performanceSampler;
    private readonly IWorkspaceDiagnosticPublisher _diagnosticPublisher;
    private readonly WorkspaceHostOptions _options;
    private EventHandler? _pendingRenderingHandler;
    private EventHandler? _pendingActivationHandler;
    private EventHandler? _pendingInputFocusHandler;
    private long _pendingFrameGeneration;
    private long _pendingActivationGeneration;
    private long _pendingInputFocusGeneration;
    private long _hotkeyReceivedTimestamp;
    private long _firstFrameTimestamp;
    private long _inputReadyTimestamp;
    private long _lastEndedGeneration;
    private long _lastHideRequestedTimestamp;
    private long _lastRestoreCompletedTimestamp;
    private ForegroundRestoreStatus _lastRestoreStatus = ForegroundRestoreStatus.InvalidTarget;
    private WorkspaceRestoreDiagnosticSnapshot _restoreDiagnostic = WorkspaceRestoreDiagnosticSnapshot.None;
    private WorkspacePresentationObservation _lastObservation;
    private bool _lastWpfInputFocus;
    private nint _windowHandle;
    private bool _disposed;

    public WorkspaceController(
        IWorkspaceWindow window,
        IWorkspaceFrameSource frameSource,
        MainWindowViewModel viewModel,
        WorkspaceSessionStateMachine stateMachine,
        IGlobalHotkeyService hotkeyService,
        IForegroundWindowService foregroundWindowService,
        IWindowPlacementService windowPlacementService,
        IPerformanceSampler performanceSampler,
        IWorkspaceDiagnosticPublisher diagnosticPublisher,
        WorkspaceHostOptions options)
    {
        _window = window;
        _frameSource = frameSource;
        _viewModel = viewModel;
        _stateMachine = stateMachine;
        _hotkeyService = hotkeyService;
        _foregroundWindowService = foregroundWindowService;
        _windowPlacementService = windowPlacementService;
        _performanceSampler = performanceSampler;
        _diagnosticPublisher = diagnosticPublisher;
        _options = options;

        _hotkeyService.Pressed += OnHotkeyPressed;
        _hotkeyService.Faulted += OnHotkeyFaulted;
        _window.HideRequested += OnHideRequested;
        _window.ExitRequested += OnExitRequested;
    }

    public async Task StartAsync()
    {
        ThrowIfDisposed();
        if (_options.PrecreateWindowHandle)
        {
            _windowHandle = _window.EnsureHandle();
            RefreshObservation(recordInViewModel: false);
            TryPublishDiagnostic();
        }

        await _hotkeyService.StartAsync();
        _viewModel.MarkReady(_hotkeyService.DisplayGesture);
    }

    public void ShowDiagnostic(Exception exception)
    {
        ThrowIfDisposed();
        Show(new HotkeyInvocation(_foregroundWindowService.Capture(), Stopwatch.GetTimestamp()));
        _viewModel.RecordFault(exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();
        _hotkeyService.Pressed -= OnHotkeyPressed;
        _hotkeyService.Faulted -= OnHotkeyFaulted;
        _window.HideRequested -= OnHideRequested;
        _window.ExitRequested -= OnExitRequested;
        TryClearDiagnostic();
    }

    private void OnHotkeyPressed(object? sender, HotkeyInvocation invocation)
    {
        if (!_disposed)
        {
            _window.PostInput(() => Toggle(invocation));
        }
    }

    private void OnHotkeyFaulted(object? sender, Exception exception)
    {
        if (!_disposed)
        {
            _window.PostNormal(() => _viewModel.RecordFault(exception));
        }
    }

    private void OnHideRequested(object? sender, WorkspaceHideRequestedEventArgs e) =>
        Hide(e.Reason);

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Hide(WorkspaceHideReason.ApplicationExit);
        Application.Current?.Shutdown();
    }

    private void Toggle(HotkeyInvocation invocation)
    {
        if (_stateMachine.State is WorkspaceState.Hidden)
        {
            Show(invocation);
            return;
        }

        Hide(WorkspaceHideReason.Hotkey);
    }

    private void Show(HotkeyInvocation invocation)
    {
        var foregroundTarget = invocation.ForegroundTarget.ProcessId == Environment.ProcessId
            ? _foregroundWindowService.Capture()
            : invocation.ForegroundTarget;
        if (foregroundTarget.ProcessId == Environment.ProcessId)
        {
            foregroundTarget = ForegroundTarget.None;
        }

        var transition = _stateMachine.BeginShow(foregroundTarget, invocation.NativeTimestamp);
        if (!transition.Accepted)
        {
            return;
        }

        _hotkeyReceivedTimestamp = invocation.NativeTimestamp;
        _firstFrameTimestamp = 0;
        _inputReadyTimestamp = 0;
        _lastObservation = default;
        _lastWpfInputFocus = false;

        AttachFirstFrame(transition.Generation, invocation.NativeTimestamp);
        AttachActivation(transition.Generation);
        AttachInputFocus(transition.Generation, invocation.NativeTimestamp);

        try
        {
            if (_windowHandle == 0)
            {
                _windowHandle = _window.EnsureHandle();
            }

            TryPublishDiagnostic();
            _windowPlacementService.ApplyFullScreen(_windowHandle, foregroundTarget);
            _window.Show();
            _ = _windowPlacementService.RequestForeground(_windowHandle);
            _ = _window.Activate();
            _ = _window.FocusInputAnchor();
            TryCompletePresentation(transition.Generation, invocation.NativeTimestamp);
            _window.PostInput(() =>
                TryCompletePresentation(transition.Generation, invocation.NativeTimestamp));
        }
        catch (Exception exception)
        {
            CleanUpFailedShow(transition.Generation, foregroundTarget, exception);
        }
    }

    private void Hide(WorkspaceHideReason reason)
    {
        var session = _stateMachine.Current;
        var transition = _stateMachine.BeginHide(reason);
        if (!transition.Accepted || session is null)
        {
            return;
        }

        _lastHideRequestedTimestamp = Stopwatch.GetTimestamp();
        BeginRestoreDiagnostic(transition.Generation, reason, session.Target, _lastHideRequestedTimestamp);
        TryPublishDiagnostic();
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();

        try
        {
            _window.Hide();
            if (_window.IsVisible)
            {
                throw new InvalidOperationException("The workspace window remained visible after Hide().");
            }

            RecordWindowHidden(transition.Generation);
        }
        catch (Exception exception)
        {
            _ = _stateMachine.RollBackHide(transition.Generation);
            RefreshObservation(recordInViewModel: false);
            TryPublishDiagnostic();
            _viewModel.RecordFault(exception);
            return;
        }

        Exception? topmostCleanupException = null;
        RecordTopmostExitAttempted(transition.Generation);
        try
        {
            _windowPlacementService.ExitTopmost(_windowHandle);
            RecordTopmostExitCompleted(transition.Generation, succeeded: true);
        }
        catch (Exception exception)
        {
            topmostCleanupException = exception;
            RecordTopmostExitCompleted(transition.Generation, succeeded: false);
        }

        var restoreResult = RestoreTarget(transition.Generation, session.Target);
        if (!_stateMachine.CompleteHide(transition.Generation))
        {
            _viewModel.RecordFault(new InvalidOperationException(
                "A stale hide completion was rejected by the workspace state machine."));
            return;
        }

        _lastEndedGeneration = transition.Generation;
        ClearCurrentSessionDiagnostics();
        RefreshObservation(recordInViewModel: false);
        TryPublishDiagnostic();
        TryRecordHidden(restoreResult);
        if (topmostCleanupException is not null)
        {
            _viewModel.RecordFault(topmostCleanupException);
        }
    }

    private void CleanUpFailedShow(long generation, ForegroundTarget target, Exception showException)
    {
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();
        _ = _stateMachine.FailShow(generation);
        var cleanupStartedTimestamp = Stopwatch.GetTimestamp();
        _lastHideRequestedTimestamp = cleanupStartedTimestamp;
        BeginRestoreDiagnostic(
            generation,
            WorkspaceHideReason.ShowFailure,
            target,
            cleanupStartedTimestamp);

        var failures = new List<Exception> { showException };
        try
        {
            _window.Hide();
            if (_window.IsVisible)
            {
                failures.Add(new InvalidOperationException(
                    "The workspace window remained visible during failed-show cleanup."));
            }
            else
            {
                RecordWindowHidden(generation);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        RecordTopmostExitAttempted(generation);
        try
        {
            _windowPlacementService.ExitTopmost(_windowHandle);
            RecordTopmostExitCompleted(generation, succeeded: true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            RecordTopmostExitCompleted(generation, succeeded: false);
        }

        var restoreResult = RestoreTarget(generation, target, failures);
        _lastEndedGeneration = generation;
        ClearCurrentSessionDiagnostics();
        RefreshObservation(recordInViewModel: false, failures);
        TryPublishDiagnostic();
        TryRecordHidden(restoreResult);
        _viewModel.RecordFault(failures.Count == 1
            ? failures[0]
            : new AggregateException(failures));
    }

    private ForegroundRestoreResult RestoreTarget(
        long generation,
        ForegroundTarget target,
        List<Exception>? failures = null)
    {
        ForegroundRestoreResult result;
        var restoreFaulted = false;
        RecordRestoreCalled(generation);
        try
        {
            result = _foregroundWindowService.Restore(target);
        }
        catch (Exception exception)
        {
            restoreFaulted = true;
            failures?.Add(exception);
            if (failures is null)
            {
                _viewModel.RecordFault(exception);
            }

            result = new ForegroundRestoreResult(ForegroundRestoreStatus.RequestDenied, 0);
        }

        RecordRestoreReturned(generation, result, restoreFaulted);

        _lastRestoreStatus = result.Status;
        _lastRestoreCompletedTimestamp = result.Status is
            ForegroundRestoreStatus.Confirmed or
            ForegroundRestoreStatus.AlreadyForeground or
            ForegroundRestoreStatus.ForegroundConfirmedFocusChanged
                ? Stopwatch.GetTimestamp()
                : 0;
        return result;
    }

    private void AttachFirstFrame(long generation, long requestedTimestamp)
    {
        DetachPendingFirstFrame();
        _pendingFrameGeneration = generation;
        _pendingRenderingHandler = (_, _) =>
        {
            if (_pendingFrameGeneration != generation)
            {
                return;
            }

            DetachPendingFirstFrame();
            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is WorkspaceState.Hidden or WorkspaceState.Hiding)
            {
                return;
            }

            _firstFrameTimestamp = Stopwatch.GetTimestamp();
            TryPublishDiagnostic();
            try
            {
                _viewModel.RecordFirstFrame(
                    Stopwatch.GetElapsedTime(requestedTimestamp, _firstFrameTimestamp),
                    _performanceSampler.Capture());
            }
            catch (Exception exception)
            {
                _viewModel.RecordFault(exception);
            }
        };
        _frameSource.Rendering += _pendingRenderingHandler;
    }

    private void AttachActivation(long generation)
    {
        DetachPendingActivation();
        _pendingActivationGeneration = generation;
        _pendingActivationHandler = (_, _) =>
        {
            if (_pendingActivationGeneration != generation)
            {
                return;
            }

            DetachPendingActivation();
            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is WorkspaceState.Hidden or WorkspaceState.Hiding)
            {
                return;
            }

            _ = _window.FocusInputAnchor();
            TryCompletePresentation(generation, _stateMachine.Current?.RequestedTimestamp ?? 0);
        };
        _window.Activated += _pendingActivationHandler;
    }

    private void AttachInputFocus(long generation, long requestedTimestamp)
    {
        DetachPendingInputFocus();
        _pendingInputFocusGeneration = generation;
        _pendingInputFocusHandler = (_, _) =>
        {
            if (_pendingInputFocusGeneration != generation)
            {
                return;
            }

            DetachPendingInputFocus();
            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is WorkspaceState.Hidden or WorkspaceState.Hiding)
            {
                return;
            }

            TryCompletePresentation(generation, requestedTimestamp);
        };
        _window.InputAnchorFocused += _pendingInputFocusHandler;
    }

    private void DetachPendingFirstFrame()
    {
        var handler = _pendingRenderingHandler;
        _pendingRenderingHandler = null;
        _pendingFrameGeneration = 0;
        if (handler is not null)
        {
            _frameSource.Rendering -= handler;
        }
    }

    private void DetachPendingActivation()
    {
        var handler = _pendingActivationHandler;
        _pendingActivationHandler = null;
        _pendingActivationGeneration = 0;
        if (handler is not null)
        {
            _window.Activated -= handler;
        }
    }

    private void DetachPendingInputFocus()
    {
        var handler = _pendingInputFocusHandler;
        _pendingInputFocusHandler = null;
        _pendingInputFocusGeneration = 0;
        if (handler is not null)
        {
            _window.InputAnchorFocused -= handler;
        }
    }

    private void TryCompletePresentation(long generation, long requestedTimestamp)
    {
        if (requestedTimestamp == 0 ||
            !_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is WorkspaceState.Hidden or WorkspaceState.Hiding)
        {
            return;
        }

        try
        {
            RefreshObservation(recordInViewModel: true);
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
            return;
        }

        TryPublishDiagnostic();
        var presentation = new WorkspacePresentationStatus(
            _lastObservation.IsVisible,
            _lastObservation.IsTopmost,
            _lastObservation.IsForeground,
            _lastObservation.HasWin32KeyboardFocus,
            _lastWpfInputFocus);
        if (!_stateMachine.CompleteShow(generation, presentation))
        {
            return;
        }

        DetachPendingActivation();
        DetachPendingInputFocus();
        _inputReadyTimestamp = Stopwatch.GetTimestamp();
        TryPublishDiagnostic();
        try
        {
            _viewModel.RecordInputReady(
                Stopwatch.GetElapsedTime(requestedTimestamp, _inputReadyTimestamp),
                _performanceSampler.Capture());
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
        }
    }

    private void RefreshObservation(bool recordInViewModel, List<Exception>? failures = null)
    {
        if (_windowHandle == 0)
        {
            _lastObservation = default;
            _lastWpfInputFocus = false;
            return;
        }

        try
        {
            _lastObservation = _windowPlacementService.Observe(_windowHandle);
            _lastWpfInputFocus = _window.HasInputAnchorFocus;
            if (recordInViewModel)
            {
                _viewModel.RecordPresentationObservation(_lastObservation, _lastWpfInputFocus);
            }
        }
        catch (Exception exception) when (failures is not null)
        {
            failures.Add(exception);
        }
    }

    private void ClearCurrentSessionDiagnostics()
    {
        _hotkeyReceivedTimestamp = 0;
        _firstFrameTimestamp = 0;
        _inputReadyTimestamp = 0;
    }

    private void TryPublishDiagnostic()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        try
        {
            _diagnosticPublisher.Publish(_windowHandle, CreateDiagnosticSnapshot());
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
        }
    }

    private WorkspaceDiagnosticSnapshot CreateDiagnosticSnapshot()
    {
        var flags = WorkspaceDiagnosticFlags.None;
        if (_lastObservation.IsVisible) flags |= WorkspaceDiagnosticFlags.Visible;
        if (_lastObservation.IsTopmost) flags |= WorkspaceDiagnosticFlags.Topmost;
        if (_lastObservation.IsForeground) flags |= WorkspaceDiagnosticFlags.Foreground;
        if (_lastObservation.HasWin32KeyboardFocus) flags |= WorkspaceDiagnosticFlags.Win32KeyboardFocus;
        if (_lastWpfInputFocus) flags |= WorkspaceDiagnosticFlags.WpfInputAnchorFocus;

        return new WorkspaceDiagnosticSnapshot(
            _stateMachine.Current?.Generation ?? 0,
            _stateMachine.State,
            flags,
            _hotkeyReceivedTimestamp,
            _firstFrameTimestamp,
            _inputReadyTimestamp,
            _lastEndedGeneration,
            _lastHideRequestedTimestamp,
            _lastRestoreCompletedTimestamp,
            _lastRestoreStatus,
            _stateMachine.Current?.Target ?? ForegroundTarget.None,
            _restoreDiagnostic);
    }

    private void BeginRestoreDiagnostic(
        long generation,
        WorkspaceHideReason reason,
        ForegroundTarget target,
        long hideRequestedTimestamp)
    {
        _restoreDiagnostic = new WorkspaceRestoreDiagnosticSnapshot(
            generation,
            reason,
            target,
            hideRequestedTimestamp,
            0,
            0,
            0,
            false,
            0,
            0,
            false,
            default);
    }

    private void RecordWindowHidden(long generation)
    {
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with
            {
                WindowHiddenTimestamp = Stopwatch.GetTimestamp(),
            };
        }
    }

    private void RecordTopmostExitAttempted(long generation)
    {
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with
            {
                TopmostExitAttemptedTimestamp = Stopwatch.GetTimestamp(),
            };
        }
    }

    private void RecordTopmostExitCompleted(long generation, bool succeeded)
    {
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with
            {
                TopmostExitCompletedTimestamp = Stopwatch.GetTimestamp(),
                TopmostExitSucceeded = succeeded,
            };
        }
    }

    private void RecordRestoreCalled(long generation)
    {
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with
            {
                RestoreCalledTimestamp = Stopwatch.GetTimestamp(),
            };
        }
    }

    private void RecordRestoreReturned(
        long generation,
        ForegroundRestoreResult result,
        bool restoreFaulted)
    {
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with
            {
                RestoreReturnedTimestamp = Stopwatch.GetTimestamp(),
                RestoreFaulted = restoreFaulted,
                Result = result,
            };
        }
    }

    private void TryClearDiagnostic()
    {
        try
        {
            _diagnosticPublisher.Clear(_windowHandle);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void TryRecordHidden(ForegroundRestoreResult restoreResult)
    {
        try
        {
            _viewModel.RecordHidden(restoreResult, _performanceSampler.Capture());
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
