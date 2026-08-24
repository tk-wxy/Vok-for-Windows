using System.Diagnostics;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Presentation;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed class WorkspaceControllerTests
{
    [Fact]
    public async Task AppliesFullScreenAndTopmostBeforeShowingWindow()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.True(rig.Window.TopmostWasAppliedWhenShown);
        Assert.True(rig.Log.IndexOf("placement.apply") < rig.Log.IndexOf("window.show"));
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
    }

    [Fact]
    public async Task ForegroundMustBeObservedBeforeInputReadyCompletes()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = false;
        rig.Window.FocusSucceeds = true;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        Assert.Equal(0, rig.ViewModel.ShowCount);

        rig.Placement.OsGatesReady = true;
        rig.Window.RaiseActivated();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(1, rig.ViewModel.ShowCount);
    }

    [Fact]
    public async Task WpfAnchorFocusMustBeObservedBeforeInputReadyCompletes()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = false;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        rig.Window.InputFocus = true;
        rig.Window.RaiseInputAnchorFocused();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(1, rig.ViewModel.ShowCount);
    }

    [Fact]
    public async Task SecondHotkeyWhileRevealingHidesAndEndsGeneration()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = false;

        rig.Hotkey.Raise(ExternalTarget);
        var generation = rig.StateMachine.Current!.Generation;
        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Null(rig.StateMachine.Current);
        Assert.Contains("window.hide", rig.Log);
        Assert.Equal(generation, rig.Publisher.Last.LastEndedGeneration);
        Assert.Equal(0, rig.Publisher.Last.Generation);
    }

    [Fact]
    public async Task HotkeyHidePublishesAnOrderedGenerationScopedRestoreRecord()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        rig.Hotkey.Raise(ExternalTarget);

        var snapshot = rig.Publisher.Last;
        var restore = snapshot.Restore;
        Assert.Equal(1, restore.Generation);
        Assert.Equal(WorkspaceHideReason.Hotkey, restore.HideReason);
        Assert.Equal(ExternalTarget, restore.Target);
        Assert.True(restore.HideRequestedTimestamp > 0);
        Assert.True(restore.WindowHiddenTimestamp >= restore.HideRequestedTimestamp);
        Assert.True(restore.TopmostExitAttemptedTimestamp >= restore.WindowHiddenTimestamp);
        Assert.True(restore.TopmostExitCompletedTimestamp >= restore.TopmostExitAttemptedTimestamp);
        Assert.True(restore.RestoreCalledTimestamp >= restore.TopmostExitCompletedTimestamp);
        Assert.True(restore.RestoreReturnedTimestamp >= restore.RestoreCalledTimestamp);
        Assert.True(restore.TopmostExitSucceeded);
        Assert.Equal(ForegroundRestoreStatus.Confirmed, restore.Result.Status);
        Assert.Equal(0, snapshot.Generation);
        Assert.Equal(WorkspaceDiagnosticFlags.None, snapshot.Flags);
    }

    [Fact]
    public async Task EscapeHideIsDistinguishedFromOtherWindowRequests()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        rig.Window.RaiseHideRequested(WorkspaceHideReason.Escape);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(WorkspaceHideReason.Escape, rig.Publisher.Last.Restore.HideReason);
        Assert.Equal(1, rig.Publisher.Last.Restore.Generation);
    }

    [Fact]
    public async Task ShowFailureRunsHideTopmostExitAndRestoreInOrder()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Window.ThrowAfterShow = true;

        rig.Hotkey.Raise(ExternalTarget);

        AssertOrdered(rig.Log, "window.show", "window.hide", "placement.exit", "foreground.restore");
        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Publisher.Last.LastEndedGeneration);
        Assert.Equal(WorkspaceHideReason.ShowFailure, rig.Publisher.Last.Restore.HideReason);
        Assert.True(rig.Publisher.Last.Restore.WindowHiddenTimestamp > 0);
        Assert.True(rig.Publisher.Last.Restore.TopmostExitSucceeded);
        Assert.True(rig.Publisher.Last.Restore.RestoreReturnedTimestamp > 0);
    }

    [Fact]
    public async Task ExitTopmostFailureDoesNotBlockRestoreOrHiddenCompletion()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Placement.ThrowOnExitTopmost = true;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Contains("foreground.restore", rig.Log);
        Assert.True(rig.Publisher.Last.IsTopmost);
        Assert.False(rig.Publisher.Last.Restore.TopmostExitSucceeded);
        Assert.True(rig.Publisher.Last.Restore.RestoreReturnedTimestamp > 0);
        Assert.Contains("topmost cleanup", rig.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreFailureIsAnExplicitHiddenDiagnosticResult()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.ThrowOnRestore = true;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(ForegroundRestoreStatus.RequestDenied, rig.Publisher.Last.LastRestoreStatus);
        Assert.Equal(0, rig.Publisher.Last.LastRestoreCompletedTimestamp);
        Assert.True(rig.Publisher.Last.Restore.RestoreCalledTimestamp > 0);
        Assert.True(rig.Publisher.Last.Restore.RestoreReturnedTimestamp >=
                    rig.Publisher.Last.Restore.RestoreCalledTimestamp);
        Assert.True(rig.Publisher.Last.Restore.RestoreFaulted);
        Assert.Contains("RequestDenied", rig.ViewModel.StatusText);
    }

    [Fact]
    public async Task SameGenerationDuplicateEventsCannotCompleteInputReadyTwice()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        rig.Window.RaiseLastRemovedActivation();
        rig.Window.RaiseLastRemovedInputFocus();
        rig.Frames.RaiseRendering();
        rig.Frames.RaiseLastRemovedRendering();

        Assert.Equal(1, rig.ViewModel.ShowCount);
        var completionTimestamps = rig.Publisher.Snapshots
            .Select(snapshot => snapshot.InputReadyTimestamp)
            .Where(timestamp => timestamp != 0)
            .Distinct();
        Assert.Single(completionTimestamps);
    }

    [Fact]
    public async Task OldGenerationActivationFocusAndRenderingCannotCompleteNewSession()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = false;
        rig.Window.FocusSucceeds = false;
        rig.Hotkey.Raise(ExternalTarget);
        rig.Hotkey.Raise(ExternalTarget);

        rig.Placement.OsGatesReady = true;
        rig.Hotkey.Raise(ExternalTarget);
        var secondGeneration = rig.StateMachine.Current!.Generation;
        rig.Window.RaiseFirstRemovedActivation();
        rig.Window.RaiseFirstRemovedInputFocus();
        rig.Frames.RaiseFirstRemovedRendering();

        Assert.Equal(2, secondGeneration);
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        Assert.Equal(0, rig.ViewModel.ShowCount);
        Assert.Equal(0, rig.Publisher.Last.InputReadyTimestamp);
        Assert.Equal(0, rig.Publisher.Last.FirstFrameTimestamp);
        Assert.Equal(1, rig.Publisher.Last.Restore.Generation);
        Assert.Equal(1, rig.Publisher.Last.LastEndedGeneration);
    }

    [Fact]
    public async Task RecapturedSelfTargetDegradesToNoRestoreTarget()
    {
        using var rig = await TestRig.CreateAsync();
        var self = new ForegroundTarget(0x999, (uint)Environment.ProcessId, 77, 0x998);
        rig.Foreground.CaptureResult = self;
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        rig.Hotkey.Raise(self);
        rig.Hotkey.Raise(self);

        Assert.Equal(ForegroundTarget.None, rig.Placement.AppliedTarget);
        Assert.Equal(ForegroundTarget.None, rig.Foreground.RestoredTarget);
        Assert.Equal(ForegroundRestoreStatus.InvalidTarget, rig.Publisher.Last.LastRestoreStatus);
    }

    private static readonly ForegroundTarget ExternalTarget = new(
        0x123,
        456,
        789,
        0x124,
        456,
        789,
        ForegroundWindowRelationship.Descendant);

    private static void AssertOrdered(IReadOnlyList<string> log, params string[] entries)
    {
        var previous = -1;
        foreach (var entry in entries)
        {
            var index = Enumerable.Range(0, log.Count)
                .FirstOrDefault(candidate => log[candidate] == entry, -1);
            Assert.True(index > previous, $"'{entry}' was missing or out of order: {string.Join(", ", log)}");
            previous = index;
        }
    }

    private sealed class TestRig : IDisposable
    {
        private TestRig()
        {
            Window = new FakeWindow(Log);
            Frames = new FakeFrameSource();
            Placement = new FakePlacement(Log, Window);
            Foreground = new FakeForeground(Log);
            Hotkey = new FakeHotkey();
            Publisher = new FakePublisher();
            StateMachine = new WorkspaceSessionStateMachine();
            ViewModel = new MainWindowViewModel();
            Controller = new WorkspaceController(
                Window,
                Frames,
                ViewModel,
                StateMachine,
                Hotkey,
                Foreground,
                Placement,
                new FakePerformanceSampler(),
                Publisher,
                new WorkspaceHostOptions(false));
        }

        public List<string> Log { get; } = [];
        public FakeWindow Window { get; }
        public FakeFrameSource Frames { get; }
        public FakePlacement Placement { get; }
        public FakeForeground Foreground { get; }
        public FakeHotkey Hotkey { get; }
        public FakePublisher Publisher { get; }
        public WorkspaceSessionStateMachine StateMachine { get; }
        public MainWindowViewModel ViewModel { get; }
        public WorkspaceController Controller { get; }

        public static async Task<TestRig> CreateAsync()
        {
            var rig = new TestRig();
            await rig.Controller.StartAsync();
            rig.Log.Clear();
            return rig;
        }

        public static async Task<TestRig> CreateVisibleAsync()
        {
            var rig = await CreateAsync();
            rig.Placement.OsGatesReady = true;
            rig.Window.FocusSucceeds = true;
            rig.Hotkey.Raise(ExternalTarget);
            Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
            rig.Log.Clear();
            return rig;
        }

        public void Dispose() => Controller.Dispose();
    }

    private sealed class FakeWindow(List<string> log) : IWorkspaceWindow
    {
        private EventHandler? _activated;
        private EventHandler? _inputFocused;
        private readonly List<EventHandler> _removedActivated = [];
        private readonly List<EventHandler> _removedInputFocused = [];

        public event EventHandler? Activated
        {
            add => _activated += value;
            remove { if (value is not null) _removedActivated.Add(value); _activated -= value; }
        }

        public event EventHandler? InputAnchorFocused
        {
            add => _inputFocused += value;
            remove { if (value is not null) _removedInputFocused.Add(value); _inputFocused -= value; }
        }

        public event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;
        public event EventHandler? ExitRequested;
        public bool IsVisible { get; private set; }
        public bool HasInputAnchorFocus => InputFocus;
        public bool InputFocus { get; set; }
        public bool FocusSucceeds { get; set; }
        public bool ThrowAfterShow { get; set; }
        public bool TopmostWasAppliedWhenShown { get; private set; }
        public Func<bool>? IsTopmost { get; set; }

        public nint EnsureHandle() { log.Add("window.ensure"); return 0x777; }
        public void Show()
        {
            log.Add("window.show");
            IsVisible = true;
            TopmostWasAppliedWhenShown = IsTopmost?.Invoke() == true;
            if (ThrowAfterShow) throw new InvalidOperationException("show failure");
        }

        public void Hide() { log.Add("window.hide"); IsVisible = false; InputFocus = false; }
        public bool Activate() { log.Add("window.activate"); return true; }
        public bool FocusInputAnchor()
        {
            log.Add("window.focus");
            InputFocus = FocusSucceeds;
            return InputFocus;
        }

        public void PostInput(Action action) { log.Add("window.post-input"); action(); }
        public void PostNormal(Action action) { log.Add("window.post-normal"); action(); }
        public void RaiseActivated() => _activated?.Invoke(this, EventArgs.Empty);
        public void RaiseInputAnchorFocused() => _inputFocused?.Invoke(this, EventArgs.Empty);
        public void RaiseFirstRemovedActivation() => _removedActivated[0](this, EventArgs.Empty);
        public void RaiseLastRemovedActivation() => _removedActivated[^1](this, EventArgs.Empty);
        public void RaiseFirstRemovedInputFocus() => _removedInputFocused[0](this, EventArgs.Empty);
        public void RaiseLastRemovedInputFocus() => _removedInputFocused[^1](this, EventArgs.Empty);

        public void RaiseHideRequested(WorkspaceHideReason reason = WorkspaceHideReason.UserAction) =>
            HideRequested?.Invoke(this, new WorkspaceHideRequestedEventArgs(reason));
        public void RaiseExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeFrameSource : IWorkspaceFrameSource
    {
        private EventHandler? _rendering;
        private readonly List<EventHandler> _removed = [];
        public event EventHandler? Rendering
        {
            add => _rendering += value;
            remove { if (value is not null) _removed.Add(value); _rendering -= value; }
        }

        public void RaiseRendering() => _rendering?.Invoke(this, EventArgs.Empty);
        public void RaiseFirstRemovedRendering() => _removed[0](this, EventArgs.Empty);
        public void RaiseLastRemovedRendering() => _removed[^1](this, EventArgs.Empty);
    }

    private sealed class FakePlacement : IWindowPlacementService
    {
        private readonly List<string> _log;
        private readonly FakeWindow _window;
        public FakePlacement(List<string> log, FakeWindow window)
        {
            _log = log;
            _window = window;
            _window.IsTopmost = () => IsTopmost;
        }

        public bool IsTopmost { get; private set; }
        public bool OsGatesReady { get; set; }
        public bool ThrowOnExitTopmost { get; set; }
        public ForegroundTarget AppliedTarget { get; private set; }

        public void ApplyFullScreen(nint workspaceWindow, ForegroundTarget target)
        {
            _log.Add("placement.apply");
            AppliedTarget = target;
            IsTopmost = true;
        }

        public bool RequestForeground(nint workspaceWindow)
        {
            _log.Add("placement.foreground");
            return OsGatesReady;
        }

        public WorkspacePresentationObservation Observe(nint workspaceWindow)
        {
            _log.Add("placement.observe");
            var workspaceHasOsFocus = OsGatesReady && _window.IsVisible;
            return new WorkspacePresentationObservation(
                _window.IsVisible,
                IsTopmost,
                workspaceHasOsFocus,
                workspaceHasOsFocus,
                workspaceHasOsFocus ? workspaceWindow : 0,
                workspaceHasOsFocus ? workspaceWindow : 0);
        }

        public void ExitTopmost(nint workspaceWindow)
        {
            _log.Add("placement.exit");
            if (ThrowOnExitTopmost) throw new InvalidOperationException("topmost cleanup failure");
            IsTopmost = false;
        }
    }

    private sealed class FakeForeground(List<string> log) : IForegroundWindowService
    {
        public ForegroundTarget CaptureResult { get; set; } = ExternalTarget;
        public ForegroundTarget RestoredTarget { get; private set; }
        public bool ThrowOnRestore { get; set; }
        public ForegroundTarget Capture() { log.Add("foreground.capture"); return CaptureResult; }
        public ForegroundRestoreResult Restore(ForegroundTarget target)
        {
            log.Add("foreground.restore");
            RestoredTarget = target;
            if (ThrowOnRestore) throw new InvalidOperationException("restore failure");
            return target.CanRestore
                ? new ForegroundRestoreResult(
                    ForegroundRestoreStatus.Confirmed,
                    target.WindowHandle,
                    target.FocusedWindowHandle,
                    SetForegroundAttempted: true,
                    SetForegroundSucceeded: true,
                    TargetWindowValid: true,
                    TargetIdentityMatches: true,
                    TargetProcessId: target.ProcessId,
                    TargetThreadId: target.ThreadId,
                    CapturedFocusWindowValid: target.FocusedWindowHandle != 0,
                    CapturedFocusIdentityMatches: target.FocusedWindowHandle != 0,
                    CapturedFocusProcessId: target.FocusedProcessId,
                    CapturedFocusThreadId: target.FocusedThreadId,
                    CapturedFocusRelationship: target.FocusedWindowRelationship,
                    ObservedFocusProcessId: target.FocusedProcessId,
                    ObservedFocusThreadId: target.FocusedThreadId,
                    ObservedFocusRelationship: target.FocusedWindowRelationship)
                : new ForegroundRestoreResult(ForegroundRestoreStatus.InvalidTarget, 0);
        }
    }

    private sealed class FakeHotkey : IGlobalHotkeyService
    {
        public event EventHandler<HotkeyInvocation>? Pressed;
        public event EventHandler<Exception>? Faulted;
        public string DisplayGesture => "test";
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Raise(ForegroundTarget target) =>
            Pressed?.Invoke(this, new HotkeyInvocation(target, Stopwatch.GetTimestamp()));
        public void Dispose() { }
        public void RaiseFault(Exception exception) => Faulted?.Invoke(this, exception);
    }

    private sealed class FakePublisher : IWorkspaceDiagnosticPublisher
    {
        public List<WorkspaceDiagnosticSnapshot> Snapshots { get; } = [];
        public WorkspaceDiagnosticSnapshot Last => Snapshots[^1];
        public void Publish(nint workspaceWindow, WorkspaceDiagnosticSnapshot snapshot) => Snapshots.Add(snapshot);
        public void Clear(nint workspaceWindow) { }
    }

    private sealed class FakePerformanceSampler : IPerformanceSampler
    {
        public PerformanceSnapshot Capture() => new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0);
    }
}
