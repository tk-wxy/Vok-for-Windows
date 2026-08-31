using System.Diagnostics;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Presentation;

namespace Vok.Hosting;

public sealed class WorkspaceController : IWorkspaceCommands, IDisposable
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
    private readonly IWorkspacePresentationService _presentationService;
    private readonly WorkspaceHostOptions _options;
    private EventHandler? _pendingRenderingHandler;
    private EventHandler? _pendingActivationHandler;
    private EventHandler? _pendingInputFocusHandler;
    private IDisposable? _revealWatchdog;
    private long _pendingFrameGeneration;
    private long _pendingActivationGeneration;
    private long _pendingInputFocusGeneration;
    private long _pendingFenceGeneration;
    private long _presentedGeneration;
    private long _revealWatchdogGeneration;
    private long _hotkeyReceivedTimestamp;
    private long _firstFrameTimestamp;
    private long _inputReadyTimestamp;
    private long _lastEndedGeneration;
    private long _lastHideRequestedTimestamp;
    private long _lastHiddenCompletedTimestamp;
    private long _lastRestoreCompletedTimestamp;
    private long _externalDragId;
    private long _externalDragWorkspaceGeneration;
    private long _pendingDragDeactivationGeneration;
    private long _pendingDragHotkeyHideGeneration;
    private ActiveHotkeyPress? _activeHotkeyPress;
    private WorkspacePasteTargetClaim? _pendingPasteTargetClaim;
    private ForegroundRestoreStatus _lastRestoreStatus = ForegroundRestoreStatus.InvalidTarget;
    private WorkspaceRestoreDiagnosticSnapshot _restoreDiagnostic = WorkspaceRestoreDiagnosticSnapshot.None;
    private WorkspacePresentationObservation _lastObservation;
    private bool _lastWpfInputFocus;
    private nint _windowHandle;
    private int _exitRequested;
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
        IWorkspacePresentationService presentationService,
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
        _presentationService = presentationService;
        _options = options;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _options.RevealWatchdogTimeout,
            TimeSpan.Zero);

        _hotkeyService.PressChanged += OnHotkeyPressChanged;
        _hotkeyService.Faulted += OnHotkeyFaulted;
        _window.HideRequested += OnHideRequested;
        _window.ApplicationDeactivated += OnApplicationDeactivated;
        _window.ExternalDragStarted += OnExternalDragStarted;
        _window.ExternalDragEnded += OnExternalDragEnded;
        _presentationService.FenceCompleted += OnPresentationFenceCompleted;
    }

    public event EventHandler? ShutdownRequested;

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
        _viewModel.MarkReady();
    }

    public void EnsureVisible(
        ForegroundTarget foregroundTarget,
        long requestedTimestamp,
        WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent)
    {
        ThrowIfDisposed();
        _window.PostNormal(() =>
            EnsureVisibleCore(foregroundTarget, requestedTimestamp, destination));
    }

    public async Task<bool> HideForLauncherActionAsync(
        CancellationToken cancellationToken = default) =>
        (await HideForActionAsync(
                WorkspaceHideReason.LauncherAction,
                cancellationToken)
            .ConfigureAwait(false)).Hidden;

    public Task<WorkspaceActionHideResult> HideForPasteActionAsync(
        CancellationToken cancellationToken = default) =>
        HideForActionAsync(WorkspaceHideReason.ClipboardPaste, cancellationToken);

    public Task<WorkspacePasteTargetClaim?> AcquirePasteTargetClaimAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WorkspacePasteTargetClaim?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionClaim = 0;
        var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        });
        try
        {
            _window.PostNormal(() =>
            {
                if (Interlocked.CompareExchange(ref transactionClaim, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    if (_disposed ||
                        Volatile.Read(ref _exitRequested) != 0 ||
                        _stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible) ||
                        _stateMachine.Current is not { } session)
                    {
                        completion.TrySetResult(null);
                        return;
                    }

                    InvalidatePasteTargetClaim();
                    var claim = new WorkspacePasteTargetClaim(
                        session.Generation,
                        session.Target);
                    _pendingPasteTargetClaim = claim;
                    completion.TrySetResult(claim);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
            {
                completion.TrySetException(exception);
            }
        }

        return AwaitPasteTargetClaimAsync(completion.Task, registration);
    }

    public Task<WorkspaceActionHideResult> HideAfterImageFileCreatedAsync(
        WorkspacePasteTargetClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WorkspaceActionHideResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionClaim = 0;
        var registration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        });
        try
        {
            _window.PostNormal(() =>
            {
                if (Interlocked.CompareExchange(ref transactionClaim, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    if (_disposed ||
                        Volatile.Read(ref _exitRequested) != 0 ||
                        !ReferenceEquals(_pendingPasteTargetClaim, claim) ||
                        !claim.IsFileCreationAuthorized ||
                        !_stateMachine.IsCurrentSession(claim.Generation) ||
                        _stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible))
                    {
                        completion.TrySetResult(WorkspaceActionHideResult.NotHidden);
                        return;
                    }

                    _pendingPasteTargetClaim = null;
                    completion.TrySetResult(Hide(
                        WorkspaceHidePolicy.For(WorkspaceHideReason.ClipboardPaste)));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
            {
                completion.TrySetException(exception);
            }
        }

        return AwaitActionHideCompletionAsync(completion.Task, registration);
    }

    private static async Task<WorkspacePasteTargetClaim?> AwaitPasteTargetClaimAsync(
        Task<WorkspacePasteTargetClaim?> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    private Task<WorkspaceActionHideResult> HideForActionAsync(
        WorkspaceHideReason reason,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<WorkspaceActionHideResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionClaim = 0;
        var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            });
        try
        {
            _window.PostNormal(() =>
            {
                // Claim before hide side effects; cancellation and the dispatcher cannot both win.
                if (Interlocked.CompareExchange(ref transactionClaim, 1, 0) != 0)
                {
                    return;
                }

                if (_disposed || Volatile.Read(ref _exitRequested) != 0)
                {
                    completion.TrySetResult(WorkspaceActionHideResult.NotHidden);
                    return;
                }

                if (_stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible))
                {
                    completion.TrySetResult(WorkspaceActionHideResult.NotHidden);
                    return;
                }

                completion.TrySetResult(Hide(WorkspaceHidePolicy.For(reason)));
            });
        }
        catch (Exception exception)
        {
            if (Interlocked.CompareExchange(ref transactionClaim, 2, 0) == 0)
            {
                completion.TrySetException(exception);
            }
        }

        return AwaitActionHideCompletionAsync(
            completion.Task,
            cancellationRegistration);
    }

    private static async Task<WorkspaceActionHideResult> AwaitActionHideCompletionAsync(
        Task<WorkspaceActionHideResult> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    public void RequestExit()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }

        try
        {
            _window.PostNormal(CompleteExitRequest);
        }
        catch
        {
            Interlocked.Exchange(ref _exitRequested, 0);
            throw;
        }
    }

    public void ReportFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ThrowIfDisposed();
        _window.PostNormal(() => _viewModel.RecordFault(exception));
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
        CancelRevealWatchdog();
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();
        _pendingFenceGeneration = 0;
        _activeHotkeyPress = null;
        InvalidatePasteTargetClaim();
        _hotkeyService.PressChanged -= OnHotkeyPressChanged;
        _hotkeyService.Faulted -= OnHotkeyFaulted;
        _window.HideRequested -= OnHideRequested;
        _window.ApplicationDeactivated -= OnApplicationDeactivated;
        _window.ExternalDragStarted -= OnExternalDragStarted;
        _window.ExternalDragEnded -= OnExternalDragEnded;
        _presentationService.FenceCompleted -= OnPresentationFenceCompleted;
        ClearExternalDragLease();
        TryClearDiagnostic();
    }

    private void OnHotkeyPressChanged(object? sender, HotkeyPressEvent pressEvent)
    {
        if (!_disposed && Volatile.Read(ref _exitRequested) == 0)
        {
            _window.PostInput(() => HandleHotkeyPressEvent(pressEvent));
        }
    }

    private void OnHotkeyFaulted(object? sender, Exception exception)
    {
        if (!_disposed)
        {
            _window.PostNormal(() => _viewModel.RecordFault(exception));
        }
    }

    private void OnHideRequested(object? sender, WorkspaceHideRequestedEventArgs e)
    {
        if (Volatile.Read(ref _exitRequested) == 0)
        {
            Hide(WorkspaceHidePolicy.For(e.Reason));
        }
    }

    private void OnApplicationDeactivated(object? sender, EventArgs e)
    {
        if (_disposed ||
            Volatile.Read(ref _exitRequested) != 0 ||
            _stateMachine.State is not WorkspaceState.Visible ||
            _stateMachine.Current is not { Generation: var generation })
        {
            return;
        }

        if (OwnsExternalDragLease(generation))
        {
            _pendingDragDeactivationGeneration = generation;
            return;
        }

        _window.PostNormal(() => TryHideAfterApplicationDeactivation(generation));
    }

    private void OnExternalDragStarted(
        object? sender,
        WorkspaceExternalDragStartedEventArgs e)
    {
        if (_disposed ||
            Volatile.Read(ref _exitRequested) != 0 ||
            _stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible) ||
            _stateMachine.Current is not { Generation: var generation })
        {
            return;
        }

        // A new genuine DragEnter is also the cleanup boundary for a missing old terminal event.
        ClearExternalDragLease();
        _externalDragId = e.DragId;
        _externalDragWorkspaceGeneration = generation;
    }

    private void OnExternalDragEnded(
        object? sender,
        WorkspaceExternalDragEndedEventArgs e)
    {
        if (_disposed || e.DragId != _externalDragId)
        {
            return;
        }

        // Claim the terminal event before activation/hide side effects; duplicates become stale.
        var generation = _externalDragWorkspaceGeneration;
        var hadDeferredDeactivation = _pendingDragDeactivationGeneration == generation;
        var hadDeferredHotkeyHide = _pendingDragHotkeyHideGeneration == generation;
        ClearExternalDragLease();

        if (!_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible))
        {
            return;
        }

        if (hadDeferredHotkeyHide)
        {
            Hide(WorkspaceHidePolicy.For(WorkspaceHideReason.Hotkey));
            return;
        }

        if (e.DropAccepted)
        {
            ReassertWorkspaceAfterAcceptedDrop(generation, hadDeferredDeactivation);
        }
        else if (hadDeferredDeactivation)
        {
            TryHideAfterApplicationDeactivation(generation);
        }
    }

    private void HandleHotkeyPressEvent(HotkeyPressEvent pressEvent)
    {
        switch (pressEvent)
        {
            case HotkeyPressStarted started:
                BeginHotkeyPress(started);
                break;
            case HotkeyPressReleased released:
                CompleteHotkeyPress(released);
                break;
            case HotkeyPressCanceled canceled:
                CancelHotkeyPress(canceled.PressGeneration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pressEvent));
        }
    }

    private void BeginHotkeyPress(HotkeyPressStarted started)
    {
        if (Volatile.Read(ref _exitRequested) != 0)
        {
            return;
        }

        _activeHotkeyPress = null;
        var workspaceWasOpenAtPress = _stateMachine.State is
            WorkspaceState.Revealing or WorkspaceState.Visible;

        if (_stateMachine.State is WorkspaceState.Hidden)
        {
            if (started.NativeTimestamp <= _lastHiddenCompletedTimestamp)
            {
                return;
            }

            Show(new HotkeyInvocation(
                started.ForegroundTarget,
                started.NativeTimestamp));
        }
        else if (!workspaceWasOpenAtPress)
        {
            return;
        }

        if (_stateMachine.Current is { Generation: var workspaceGeneration })
        {
            _activeHotkeyPress = new ActiveHotkeyPress(
                started.PressGeneration,
                workspaceGeneration,
                workspaceWasOpenAtPress);
        }
    }

    private void CompleteHotkeyPress(HotkeyPressReleased released)
    {
        var active = _activeHotkeyPress;
        if (active is null || active.PressGeneration != released.PressGeneration)
        {
            return;
        }

        _activeHotkeyPress = null;
        if (_stateMachine.Current?.Generation != active.WorkspaceGeneration ||
            _stateMachine.State is not (WorkspaceState.Revealing or WorkspaceState.Visible))
        {
            return;
        }

        if (HotkeyPressSemantics.GetReleaseAction(
                released.HeldDuration,
                active.WorkspaceWasOpenAtPress) is HotkeyReleaseAction.Hide)
        {
            if (OwnsExternalDragLease(active.WorkspaceGeneration))
            {
                if (!active.WorkspaceWasOpenAtPress)
                {
                    _pendingDragHotkeyHideGeneration = active.WorkspaceGeneration;
                    return;
                }
            }

            Hide(WorkspaceHidePolicy.For(WorkspaceHideReason.Hotkey));
        }
    }

    private void CancelHotkeyPress(long pressGeneration)
    {
        if (_activeHotkeyPress?.PressGeneration == pressGeneration)
        {
            _activeHotkeyPress = null;
        }
    }

    private void EnsureVisibleCore(
        ForegroundTarget foregroundTarget,
        long requestedTimestamp,
        WorkspaceDestination destination)
    {
        if (_disposed || Volatile.Read(ref _exitRequested) != 0)
        {
            return;
        }

        if (destination is WorkspaceDestination.Settings)
        {
            _viewModel.NavigateTo(WorkspaceSection.Settings);
        }

        var invocation = new HotkeyInvocation(foregroundTarget, requestedTimestamp);
        if (_stateMachine.State is WorkspaceState.Hidden)
        {
            Show(invocation);
            return;
        }

        if (_stateMachine.State is not WorkspaceState.Visible)
        {
            return;
        }

        var remainsForeground = false;
        try
        {
            RefreshObservation(recordInViewModel: false);
            remainsForeground = _lastObservation.IsForeground;
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
        }

        if (remainsForeground)
        {
            return;
        }

        Hide(
            WorkspaceHidePolicy.For(WorkspaceHideReason.ForegroundLost),
            foregroundTarget);
        if (_stateMachine.State is WorkspaceState.Hidden)
        {
            Show(invocation);
        }
    }

    private void CompleteExitRequest()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        if (_stateMachine.State is not WorkspaceState.Hidden)
        {
            Hide(WorkspaceHidePolicy.For(WorkspaceHideReason.ApplicationExit));
        }

        PublishShutdownRequested();
    }

    private void PublishShutdownRequested()
    {
        var handlers = ShutdownRequested?.GetInvocationList();
        if (handlers is null)
        {
            _viewModel.RecordFault(new InvalidOperationException(
                "The workspace exit transaction completed without an application lifetime owner."));
            Interlocked.Exchange(ref _exitRequested, 0);
            return;
        }

        var delivered = false;
        foreach (var handler in handlers.Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
                delivered = true;
            }
            catch (Exception exception)
            {
                _viewModel.RecordFault(exception);
            }
        }

        if (!delivered)
        {
            Interlocked.Exchange(ref _exitRequested, 0);
        }
    }

    private void TryHideAfterApplicationDeactivation(long generation)
    {
        if (_disposed ||
            Volatile.Read(ref _exitRequested) != 0 ||
            !_stateMachine.IsCurrentVisible(generation))
        {
            return;
        }

        if (OwnsExternalDragLease(generation))
        {
            _pendingDragDeactivationGeneration = generation;
            return;
        }

        ForegroundTarget foreground;
        try
        {
            foreground = _foregroundWindowService.Capture();
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
            foreground = ForegroundTarget.None;
        }

        Hide(
            WorkspaceHidePolicy.For(WorkspaceHideReason.ForegroundLost),
            foreground);
    }

    private void Show(HotkeyInvocation invocation)
    {
        ClearExternalDragLease();
        InvalidatePasteTargetClaim();
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
        _pendingFenceGeneration = 0;
        _presentedGeneration = 0;

        try
        {
            AttachFirstFrame(transition.Generation);
            AttachActivation(transition.Generation);
            AttachInputFocus(transition.Generation, invocation.NativeTimestamp);
            StartRevealWatchdog(transition.Generation, foregroundTarget);

            if (_windowHandle == 0)
            {
                _windowHandle = _window.EnsureHandle();
            }

            _presentationService.SetCloaked(_windowHandle, cloaked: true);
            if (!_presentationService.IsCloaked(_windowHandle))
            {
                throw new InvalidOperationException(
                    "DWM did not cloak the workspace before its reveal transaction.");
            }

            TryPublishDiagnostic();
            _windowPlacementService.ApplyFullScreen(_windowHandle, foregroundTarget);
            _window.Show();
        }
        catch (Exception exception)
        {
            CleanUpFailedShow(transition.Generation, foregroundTarget, exception);
        }
    }

    private WorkspaceActionHideResult Hide(
        WorkspaceHidePolicy policy,
        ForegroundTarget currentForeground = default)
    {
        ClearExternalDragLease();
        InvalidatePasteTargetClaim();
        var session = _stateMachine.Current;
        var transition = _stateMachine.BeginHide(policy.Reason);
        if (!transition.Accepted || session is null)
        {
            return WorkspaceActionHideResult.NotHidden;
        }

        _lastHideRequestedTimestamp = Stopwatch.GetTimestamp();
        BeginRestoreDiagnostic(
            transition.Generation,
            policy.Reason,
            session.Target,
            _lastHideRequestedTimestamp);
        TryPublishDiagnostic();
        CancelRevealWatchdog();
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();
        _pendingFenceGeneration = 0;

        var cleanupFailures = new List<Exception>();

        Exception? hideFailure = null;
        try
        {
            _window.Hide();
        }
        catch (Exception exception)
        {
            hideFailure = exception;
        }

        if (_window.IsVisible)
        {
            hideFailure ??= new InvalidOperationException(
                "The workspace window remained visible after Hide().");
            if (!_stateMachine.RollBackHide(transition.Generation))
            {
                _viewModel.RecordFault(new AggregateException(
                    hideFailure,
                    new InvalidOperationException(
                        "The failed hide could not roll back its owned generation.")));
                return WorkspaceActionHideResult.NotHidden;
            }

            ResumeRevealAfterFailedHide(session);
            RefreshObservation(recordInViewModel: false);
            TryPublishDiagnostic();
            _viewModel.RecordFault(hideFailure);
            return WorkspaceActionHideResult.NotHidden;
        }

        if (hideFailure is not null)
        {
            cleanupFailures.Add(hideFailure);
        }

        RecordWindowHidden(transition.Generation);
        TryClearCloakWhenHidden(cleanupFailures);

        RecordTopmostExitAttempted(transition.Generation);
        try
        {
            _windowPlacementService.ExitTopmost(_windowHandle);
            RecordTopmostExitCompleted(transition.Generation, succeeded: true);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
            RecordTopmostExitCompleted(transition.Generation, succeeded: false);
        }

        var restoreResult = policy.TargetRestoreMode switch
        {
            WorkspaceTargetRestoreMode.RestoreCapturedTarget =>
                RestoreTarget(transition.Generation, session.Target),
            WorkspaceTargetRestoreMode.PreserveCurrentForeground =>
                RecordRestoreNotRequested(transition.Generation, currentForeground),
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.TargetRestoreMode,
                null),
        };
        if (!_stateMachine.CompleteHide(transition.Generation))
        {
            _viewModel.RecordFault(new InvalidOperationException(
                "A stale hide completion was rejected by the workspace state machine."));
            return WorkspaceActionHideResult.NotHidden;
        }

        _lastEndedGeneration = transition.Generation;
        _lastHiddenCompletedTimestamp = Stopwatch.GetTimestamp();
        ClearCurrentSessionDiagnostics();
        RefreshObservation(recordInViewModel: false);
        TryPublishDiagnostic();
        TryRecordHidden(restoreResult);
        if (cleanupFailures.Count != 0)
        {
            _viewModel.RecordFault(cleanupFailures.Count == 1
                ? cleanupFailures[0]
                : new AggregateException(cleanupFailures));
        }

        return new WorkspaceActionHideResult(
            Hidden: true,
            session.Target,
            restoreResult);
    }

    private void CleanUpFailedShow(long generation, ForegroundTarget target, Exception showException)
    {
        if (!_stateMachine.FailShow(generation))
        {
            return;
        }

        InvalidatePasteTargetClaim();

        CancelRevealWatchdog();
        DetachPendingFirstFrame();
        DetachPendingActivation();
        DetachPendingInputFocus();
        if (_pendingFenceGeneration == generation)
        {
            _pendingFenceGeneration = 0;
        }

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
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (_window.IsVisible)
        {
            failures.Add(new InvalidOperationException(
                "The workspace window remained visible during failed-show cleanup."));
        }
        else
        {
            RecordWindowHidden(generation);
            TryClearCloakWhenHidden(failures);
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
        _lastHiddenCompletedTimestamp = Stopwatch.GetTimestamp();
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

    private ForegroundRestoreResult RecordRestoreNotRequested(
        long generation,
        ForegroundTarget currentForeground)
    {
        var result = new ForegroundRestoreResult(
            ForegroundRestoreStatus.NotRequested,
            currentForeground.WindowHandle,
            currentForeground.FocusedWindowHandle,
            ObservedFocusProcessId: currentForeground.FocusedProcessId,
            ObservedFocusThreadId: currentForeground.FocusedThreadId,
            ObservedFocusRelationship: currentForeground.FocusedWindowRelationship);
        if (_restoreDiagnostic.Generation == generation)
        {
            _restoreDiagnostic = _restoreDiagnostic with { Result = result };
        }

        _lastRestoreStatus = result.Status;
        _lastRestoreCompletedTimestamp = 0;
        return result;
    }

    private bool OwnsExternalDragLease(long generation) =>
        _externalDragId != 0 && _externalDragWorkspaceGeneration == generation;

    private void ClearExternalDragLease()
    {
        _externalDragId = 0;
        _externalDragWorkspaceGeneration = 0;
        _pendingDragDeactivationGeneration = 0;
        _pendingDragHotkeyHideGeneration = 0;
    }

    private void ReassertWorkspaceAfterAcceptedDrop(
        long generation,
        bool hadDeferredDeactivation)
    {
        var activationFailed = false;
        try
        {
            _ = _windowPlacementService.RequestForeground(_windowHandle);
            _ = _window.Activate();
            _ = _window.FocusInputAnchor();
        }
        catch (Exception exception)
        {
            activationFailed = true;
            _viewModel.RecordFault(exception);
        }

        try
        {
            _window.PostInput(() => VerifyWorkspaceAfterAcceptedDrop(
                generation,
                hadDeferredDeactivation || activationFailed));
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
            if (hadDeferredDeactivation || activationFailed)
            {
                TryHideAfterApplicationDeactivation(generation);
            }
        }
    }

    private void VerifyWorkspaceAfterAcceptedDrop(
        long generation,
        bool hideIfUnconfirmed)
    {
        if (_disposed ||
            !_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is not WorkspaceState.Visible)
        {
            return;
        }

        try
        {
            RefreshObservation(recordInViewModel: false);
            if (_lastObservation.IsForeground && _window.HasInputAnchorFocus)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
            hideIfUnconfirmed = true;
        }

        if (hideIfUnconfirmed)
        {
            TryHideAfterApplicationDeactivation(generation);
        }
    }

    private sealed record ActiveHotkeyPress(
        long PressGeneration,
        long WorkspaceGeneration,
        bool WorkspaceWasOpenAtPress);

    private void AttachFirstFrame(long generation)
    {
        DetachPendingFirstFrame();
        _pendingFrameGeneration = generation;
        _pendingRenderingHandler = (_, _) =>
        {
            if (_pendingFrameGeneration != generation)
            {
                return;
            }

            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is not WorkspaceState.Revealing ||
                !_window.IsVisible)
            {
                return;
            }

            DetachPendingFirstFrame();

            // CompositionTarget.Rendering runs before MediaContext renders the frame.
            // Background priority resumes only after that dispatcher render operation returns.
            try
            {
                _window.PostBackground(() => RequestPresentationFence(generation));
            }
            catch (Exception exception)
            {
                CleanUpFailedShow(
                    generation,
                    _stateMachine.Current?.Target ?? ForegroundTarget.None,
                    exception);
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

            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is not WorkspaceState.Revealing ||
                _presentedGeneration != generation)
            {
                return;
            }

            DetachPendingActivation();
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

            if (!_stateMachine.IsCurrentSession(generation) ||
                _stateMachine.State is not WorkspaceState.Revealing ||
                _presentedGeneration != generation)
            {
                return;
            }

            DetachPendingInputFocus();
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

    private void OnPresentationFenceCompleted(
        object? sender,
        WorkspacePresentationFenceCompletedEventArgs e)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            _window.PostNormal(() => CompletePresentationFence(e));
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void RequestPresentationFence(long generation)
    {
        if (_disposed ||
            _pendingFenceGeneration != 0 ||
            !_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is not WorkspaceState.Revealing ||
            !_window.IsVisible)
        {
            return;
        }

        // Claim before enqueueing: a test double or future implementation may complete inline.
        _pendingFenceGeneration = generation;
        try
        {
            if (!_presentationService.TryRequestFence(generation))
            {
                throw new InvalidOperationException(
                    "The DWM presentation fence queue is full.");
            }
        }
        catch (Exception exception)
        {
            if (_pendingFenceGeneration == generation)
            {
                _pendingFenceGeneration = 0;
            }

            CleanUpFailedShow(
                generation,
                _stateMachine.Current?.Target ?? ForegroundTarget.None,
                exception);
        }
    }

    private void CompletePresentationFence(
        WorkspacePresentationFenceCompletedEventArgs completion)
    {
        var generation = completion.Generation;
        if (_pendingFenceGeneration != generation)
        {
            return;
        }

        // Claim before changing cloak/focus so duplicate or reentrant completion is harmless.
        _pendingFenceGeneration = 0;
        if (!_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is not WorkspaceState.Revealing)
        {
            return;
        }

        var session = _stateMachine.Current!;
        if (completion.Exception is not null)
        {
            CleanUpFailedShow(generation, session.Target, completion.Exception);
            return;
        }

        try
        {
            _presentationService.SetCloaked(_windowHandle, cloaked: false);
            if (_presentationService.IsCloaked(_windowHandle))
            {
                throw new InvalidOperationException(
                    "DWM kept the workspace cloaked after its content-presented fence completed.");
            }

            _presentedGeneration = generation;
            _firstFrameTimestamp = Stopwatch.GetTimestamp();

            _ = _windowPlacementService.RequestForeground(_windowHandle);
            _ = _window.Activate();
            _ = _window.FocusInputAnchor();
            TryCompletePresentation(generation, session.RequestedTimestamp);
            _window.PostInput(() =>
                TryCompletePresentation(generation, session.RequestedTimestamp));
        }
        catch (Exception exception)
        {
            CleanUpFailedShow(generation, session.Target, exception);
        }
    }

    private void StartRevealWatchdog(long generation, ForegroundTarget target)
    {
        CancelRevealWatchdog();
        _revealWatchdogGeneration = generation;
        var registration = _window.ScheduleNormal(
            _options.RevealWatchdogTimeout,
            () => OnRevealWatchdogElapsed(generation, target));
        if (_revealWatchdogGeneration == generation &&
            _stateMachine.IsCurrentSession(generation) &&
            _stateMachine.State is WorkspaceState.Revealing)
        {
            _revealWatchdog = registration;
        }
        else
        {
            registration.Dispose();
        }
    }

    private void OnRevealWatchdogElapsed(long generation, ForegroundTarget target)
    {
        if (_revealWatchdogGeneration != generation ||
            !_stateMachine.IsCurrentSession(generation) ||
            _stateMachine.State is not WorkspaceState.Revealing)
        {
            return;
        }

        // Claim before cleanup so a same-generation fence completion cannot also finish the show.
        _revealWatchdogGeneration = 0;
        var registration = _revealWatchdog;
        _revealWatchdog = null;
        registration?.Dispose();
        CleanUpFailedShow(
            generation,
            target,
            new TimeoutException(
                $"The workspace reveal did not complete within the " +
                $"{_options.RevealWatchdogTimeout.TotalSeconds:0.###}-second failure watchdog."));
    }

    private void CancelRevealWatchdog()
    {
        _revealWatchdogGeneration = 0;
        var registration = _revealWatchdog;
        _revealWatchdog = null;
        registration?.Dispose();
    }

    private void TryClearCloakWhenHidden(List<Exception> failures)
    {
        if (_windowHandle == 0 || _window.IsVisible)
        {
            return;
        }

        try
        {
            _presentationService.SetCloaked(_windowHandle, cloaked: false);
            if (_presentationService.IsCloaked(_windowHandle))
            {
                failures.Add(new InvalidOperationException(
                    "The hidden workspace HWND remained DWM-cloaked after cleanup."));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void ResumeRevealAfterFailedHide(WorkspaceSession session)
    {
        if (!_stateMachine.IsCurrentSession(session.Generation) ||
            _stateMachine.State is not WorkspaceState.Revealing)
        {
            return;
        }

        try
        {
            _presentationService.SetCloaked(_windowHandle, cloaked: true);
            if (!_presentationService.IsCloaked(_windowHandle))
            {
                throw new InvalidOperationException(
                    "The workspace could not restore its reveal cloak after Hide() failed.");
            }

            AttachFirstFrame(session.Generation);
            AttachActivation(session.Generation);
            AttachInputFocus(session.Generation, session.RequestedTimestamp);
            StartRevealWatchdog(session.Generation, session.Target);
        }
        catch (Exception exception)
        {
            CleanUpFailedShow(session.Generation, session.Target, exception);
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
            RefreshObservation(recordInViewModel: false);
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
            return;
        }

        var presentation = new WorkspacePresentationStatus(
            _lastObservation.IsVisible,
            _lastObservation.IsTopmost,
            _lastObservation.IsForeground,
            _lastObservation.HasWin32KeyboardFocus,
            _lastWpfInputFocus,
            _presentedGeneration == generation);
        if (!_stateMachine.CompleteShow(generation, presentation))
        {
            return;
        }

        DetachPendingActivation();
        DetachPendingInputFocus();
        CancelRevealWatchdog();
        _inputReadyTimestamp = Stopwatch.GetTimestamp();
        try
        {
            _window.PostBackground(() => RecordCompletedPresentation(generation));
        }
        catch (Exception exception)
        {
            _viewModel.RecordFault(exception);
        }
    }

    private void RecordCompletedPresentation(long generation)
    {
        if (!_stateMachine.IsCurrentVisible(generation))
        {
            return;
        }

        TryPublishDiagnostic();
        try
        {
            var requestedTimestamp = _stateMachine.Current!.RequestedTimestamp;
            _viewModel.RecordFirstFrame(
                Stopwatch.GetElapsedTime(requestedTimestamp, _firstFrameTimestamp));
            _viewModel.RecordInputReady(
                Stopwatch.GetElapsedTime(requestedTimestamp, _inputReadyTimestamp));
            _viewModel.RecordPerformanceSnapshot(_performanceSampler.Capture());
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
        _presentedGeneration = 0;
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
        if (_stateMachine.Current is { Generation: var generation } &&
            _presentedGeneration == generation)
        {
            flags |= WorkspaceDiagnosticFlags.Presented;
        }

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

    private void InvalidatePasteTargetClaim()
    {
        var claim = _pendingPasteTargetClaim;
        _pendingPasteTargetClaim = null;
        claim?.Invalidate();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
