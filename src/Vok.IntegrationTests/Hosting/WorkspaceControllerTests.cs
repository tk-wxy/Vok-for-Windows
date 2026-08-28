using System.Diagnostics;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Presentation;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Features.Settings;
using Vok.Hotkeys;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed class WorkspaceControllerTests
{
    [Fact]
    public async Task ShortPressFromHiddenShowsAndRemainsVisibleAfterRelease()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Hotkey.RaiseReleased(
            pressGeneration,
            HotkeyPressSemantics.TapMaxDuration);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);
        Assert.Equal(0, rig.Foreground.RestoreCallCount);
    }

    [Fact]
    public async Task LongPressFromHiddenShowsUntilReleaseThenHidesAndRestores()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Hotkey.RaiseReleased(
            pressGeneration,
            HotkeyPressSemantics.TapMaxDuration + TimeSpan.FromMilliseconds(1));

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
        Assert.Equal(WorkspaceHideReason.Hotkey, rig.Publisher.Last.Restore.HideReason);
    }

    [Fact]
    public async Task PressFromVisibleWaitsForReleaseBeforeHiding()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Hotkey.RaiseReleased(pressGeneration, TimeSpan.FromMilliseconds(50));

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
    }

    [Fact]
    public async Task ReleaseFromEndedPressCannotHideNewWorkspaceGeneration()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;
        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);
        var firstWorkspaceGeneration = rig.StateMachine.Current!.Generation;
        rig.Foreground.CaptureResult = SelectedExternalTarget;

        rig.Window.RaiseApplicationDeactivated();
        rig.Controller.EnsureVisible(
            NewSessionTarget,
            Stopwatch.GetTimestamp());
        var secondWorkspaceGeneration = rig.StateMachine.Current!.Generation;

        rig.Hotkey.RaiseReleased(
            pressGeneration,
            TimeSpan.FromSeconds(1));

        Assert.NotEqual(firstWorkspaceGeneration, secondWorkspaceGeneration);
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(secondWorkspaceGeneration, rig.StateMachine.Current!.Generation);
        Assert.Equal(NewSessionTarget, rig.StateMachine.Current.Target);
    }

    [Fact]
    public async Task CanceledPressFromVisibleDoesNotHideWorkspace()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        rig.Hotkey.RaiseCanceled(pressGeneration);
        rig.Hotkey.RaiseReleased(pressGeneration, TimeSpan.FromSeconds(1));

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);
    }

    [Fact]
    public async Task StaleReleaseCannotClearOrCompleteNewerActivePress()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var firstPress = rig.Hotkey.RaiseStarted(ExternalTarget);
        var secondPress = rig.Hotkey.RaiseStarted(ExternalTarget);

        rig.Hotkey.RaiseReleased(firstPress, TimeSpan.FromSeconds(1));

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Hotkey.RaiseReleased(secondPress, TimeSpan.FromMilliseconds(25));

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
    }

    [Fact]
    public async Task DuplicateReleaseCannotExecuteHideTwice()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        rig.Hotkey.RaiseReleased(pressGeneration, TimeSpan.FromMilliseconds(25));
        rig.Hotkey.RaiseReleased(pressGeneration, TimeSpan.FromMilliseconds(25));

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
    }

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
    public async Task TrayShowWhileHiddenUsesTheNormalPresentationTransaction()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        rig.Controller.EnsureVisible(
            ExternalTarget,
            Stopwatch.GetTimestamp());

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(ExternalTarget, rig.Placement.AppliedTarget);
        Assert.True(rig.Window.TopmostWasAppliedWhenShown);
        Assert.True(rig.Log.IndexOf("placement.apply") < rig.Log.IndexOf("window.show"));
    }

    [Fact]
    public async Task TrayShowWhileAlreadyForegroundIsIdempotent()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var generation = rig.StateMachine.Current!.Generation;

        rig.Controller.EnsureVisible(
            SelectedExternalTarget,
            Stopwatch.GetTimestamp());

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(generation, rig.StateMachine.Current!.Generation);
        Assert.DoesNotContain("window.hide", rig.Log);
        Assert.DoesNotContain("window.show", rig.Log);
    }

    [Fact]
    public async Task QueuedForegroundLossCompletesBeforeTraySettingsStartsANewGeneration()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.QueueNormalPosts = true;
        rig.Foreground.CaptureResult = SelectedExternalTarget;

        rig.Window.RaiseApplicationDeactivated();
        rig.Controller.EnsureVisible(
            SelectedExternalTarget,
            Stopwatch.GetTimestamp(),
            WorkspaceDestination.Settings);
        rig.Window.DrainNormalPosts();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(2, rig.StateMachine.Current!.Generation);
        Assert.Equal(WorkspaceSection.Settings, rig.ViewModel.CurrentSection);
        Assert.Equal(SelectedExternalTarget, rig.Placement.AppliedTarget);
        Assert.Equal(0, rig.Foreground.RestoreCallCount);
    }

    [Fact]
    public async Task ExitRequestClaimsOnceAndBlocksLaterShowRequests()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var shutdownCount = 0;
        rig.Controller.ShutdownRequested += (_, _) => shutdownCount++;

        rig.Controller.RequestExit();
        rig.Controller.RequestExit();
        rig.Hotkey.Raise(SelectedExternalTarget);
        rig.Controller.EnsureVisible(
            SelectedExternalTarget,
            Stopwatch.GetTimestamp());

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, shutdownCount);
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
        Assert.Equal(WorkspaceHideReason.ApplicationExit, rig.Publisher.Last.Restore.HideReason);
    }

    [Fact]
    public async Task ExitClaimWinsAgainstQueuedForegroundLoss()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.QueueNormalPosts = true;
        var shutdownCount = 0;
        rig.Controller.ShutdownRequested += (_, _) => shutdownCount++;

        rig.Window.RaiseApplicationDeactivated();
        rig.Controller.RequestExit();
        rig.Window.DrainNormalPosts();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, shutdownCount);
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
        Assert.Equal(WorkspaceHideReason.ApplicationExit, rig.Publisher.Last.Restore.HideReason);
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
    public async Task ExternalForegroundLossHidesWithoutRestoringCapturedTarget()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;

        rig.Window.RaiseApplicationDeactivated();

        var snapshot = rig.Publisher.Last;
        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        AssertOrdered(rig.Log, "foreground.capture", "window.hide", "placement.exit");
        Assert.DoesNotContain("foreground.restore", rig.Log);
        Assert.Equal(0, rig.Foreground.RestoreCallCount);
        Assert.Equal(WorkspaceHideReason.ForegroundLost, snapshot.Restore.HideReason);
        Assert.Equal(ForegroundRestoreStatus.NotRequested, snapshot.Restore.Result.Status);
        Assert.Equal(SelectedExternalTarget.WindowHandle, snapshot.Restore.Result.ObservedForeground);
        Assert.Equal(0, snapshot.Restore.RestoreCalledTimestamp);
        Assert.Equal(0, snapshot.Restore.RestoreReturnedTimestamp);
        Assert.True(snapshot.Restore.TopmostExitSucceeded);
        Assert.Equal(WorkspaceDiagnosticFlags.None, snapshot.Flags);
    }

    [Fact]
    public async Task ExternalDragLeaseDefersForegroundLossUntilCanceledDragEnds()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;

        rig.Window.RaiseExternalDragStarted(41);
        rig.Window.RaiseApplicationDeactivated();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Window.RaiseExternalDragEnded(41, dropAccepted: false);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(WorkspaceHideReason.ForegroundLost, rig.Publisher.Last.Restore.HideReason);
        Assert.Equal(0, rig.Foreground.RestoreCallCount);
    }

    [Fact]
    public async Task AcceptedExternalDropReassertsFocusAndConsumesDeferredDeactivation()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        rig.Window.RaiseExternalDragStarted(42);
        rig.Window.RaiseApplicationDeactivated();
        rig.Window.RaiseExternalDragEnded(42, dropAccepted: true);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Contains("placement.foreground", rig.Log);
        Assert.Contains("window.activate", rig.Log);
        Assert.Contains("window.focus", rig.Log);
        Assert.DoesNotContain("window.hide", rig.Log);
    }

    [Fact]
    public async Task DeferredHotkeyHideWinsOnceWhenExternalDragTerminates()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.RaiseExternalDragStarted(43);
        var pressGeneration = rig.Hotkey.RaiseStarted(ExternalTarget);

        rig.Hotkey.RaiseReleased(pressGeneration, TimeSpan.FromMilliseconds(25));

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.DoesNotContain("window.hide", rig.Log);

        rig.Window.RaiseExternalDragEnded(43, dropAccepted: true);
        rig.Window.RaiseExternalDragEnded(43, dropAccepted: true);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(WorkspaceHideReason.Hotkey, rig.Publisher.Last.Restore.HideReason);
    }

    [Fact]
    public async Task StaleDragTerminalCannotAffectANewerWorkspaceGeneration()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.RaiseExternalDragStarted(44);
        rig.Window.RaiseHideRequested(WorkspaceHideReason.Escape);
        rig.Hotkey.Raise(NewSessionTarget);
        var generation = rig.StateMachine.Current!.Generation;
        rig.Log.Clear();

        rig.Window.RaiseExternalDragEnded(44, dropAccepted: false);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(generation, rig.StateMachine.Current!.Generation);
        Assert.DoesNotContain("window.hide", rig.Log);
    }

    [Fact]
    public async Task LauncherActionCompletesHideTransactionBeforeReportingSuccess()
    {
        using var rig = await TestRig.CreateVisibleAsync();

        var hidden = await rig.Controller.HideForLauncherActionAsync(
            TestContext.Current.CancellationToken);

        Assert.True(hidden);
        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        AssertOrdered(rig.Log, "window.hide", "placement.exit", "foreground.restore");
        Assert.Equal(WorkspaceHideReason.LauncherAction, rig.Publisher.Last.Restore.HideReason);
    }

    [Fact]
    public async Task LauncherActionIsRejectedWhenHideRollsBack()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.HideFailuresRemaining = 1;

        var hidden = await rig.Controller.HideForLauncherActionAsync(
            TestContext.Current.CancellationToken);

        Assert.False(hidden);
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
    }

    [Fact]
    public async Task ApplicationDeactivationStillHidesWhenForegroundObservationFails()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.ThrowOnCapture = true;

        rig.Window.RaiseApplicationDeactivated();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Contains("window.hide", rig.Log);
        Assert.DoesNotContain("foreground.restore", rig.Log);
        Assert.Equal(ForegroundRestoreStatus.NotRequested, rig.Publisher.Last.Restore.Result.Status);
    }

    [Fact]
    public async Task ApplicationDeactivationWithMissingForegroundStillHides()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = ForegroundTarget.None;

        rig.Window.RaiseApplicationDeactivated();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Contains("window.hide", rig.Log);
        Assert.DoesNotContain("foreground.restore", rig.Log);
        Assert.Equal(ForegroundRestoreStatus.NotRequested, rig.Publisher.Last.Restore.Result.Status);
        Assert.Equal(0, rig.Publisher.Last.Restore.Result.ObservedForeground);
        Assert.Equal(1, rig.Foreground.CaptureCallCount);
    }

    [Fact]
    public async Task ApplicationDeactivationIsIgnoredOutsideVisibleState()
    {
        using var rig = await TestRig.CreateAsync();

        rig.Window.RaiseApplicationDeactivated();
        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);

        rig.Placement.OsGatesReady = false;
        rig.Hotkey.Raise(ExternalTarget);
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        rig.Window.RaiseApplicationDeactivated();
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(0, rig.Foreground.CaptureCallCount);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
    }

    [Theory]
    [InlineData(WorkspaceHideReason.Hotkey)]
    [InlineData(WorkspaceHideReason.Escape)]
    public async Task ApplicationDeactivationRaisedByExplicitHideDoesNotStartSecondTransaction(
        WorkspaceHideReason explicitReason)
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Window.RaiseApplicationDeactivatedDuringHide = true;

        if (explicitReason is WorkspaceHideReason.Hotkey)
        {
            rig.Hotkey.Raise(ExternalTarget);
        }
        else
        {
            rig.Window.RaiseHideRequested(WorkspaceHideReason.Escape);
        }

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(explicitReason, rig.Publisher.Last.Restore.HideReason);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(1, rig.Log.Count(entry => entry == "placement.exit"));
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
        Assert.Equal(0, rig.Foreground.CaptureCallCount);
    }

    [Fact]
    public async Task OldGenerationForegroundLossCannotHideNewSession()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;
        rig.Window.QueueNormalPosts = true;
        rig.Window.RaiseApplicationDeactivated();

        rig.Hotkey.Raise(ExternalTarget);
        rig.Window.QueueNormalPosts = false;
        rig.Hotkey.Raise(NewSessionTarget);
        var secondGeneration = rig.StateMachine.Current!.Generation;

        rig.Window.DrainNormalPosts();

        Assert.Equal(2, secondGeneration);
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(NewSessionTarget, rig.Placement.AppliedTarget);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
    }

    [Theory]
    [InlineData(WorkspaceHideReason.Hotkey)]
    [InlineData(WorkspaceHideReason.Escape)]
    public async Task ExplicitHideWinsAgainstQueuedForegroundLossWithoutDoubleExecution(
        WorkspaceHideReason explicitReason)
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;
        rig.Window.QueueNormalPosts = true;
        rig.Window.RaiseApplicationDeactivated();

        if (explicitReason is WorkspaceHideReason.Hotkey)
        {
            rig.Hotkey.Raise(ExternalTarget);
        }
        else
        {
            rig.Window.RaiseHideRequested(WorkspaceHideReason.Escape);
        }

        rig.Window.DrainNormalPosts();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(explicitReason, rig.Publisher.Last.Restore.HideReason);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(1, rig.Log.Count(entry => entry == "placement.exit"));
        Assert.Equal(1, rig.Foreground.RestoreCallCount);
    }

    [Fact]
    public async Task ForegroundLossSuppressesHotkeyAlreadyQueuedForTheEndedSession()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;
        rig.Window.QueueInputPosts = true;

        rig.Hotkey.Raise(NewSessionTarget);
        rig.Window.RaiseApplicationDeactivated();
        rig.Window.DrainInputPosts();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(1, rig.Log.Count(entry => entry == "window.hide"));
        Assert.Equal(0, rig.Foreground.RestoreCallCount);
        Assert.Equal(WorkspaceHideReason.ForegroundLost, rig.Publisher.Last.Restore.HideReason);
    }

    [Fact]
    public async Task ShowAfterForegroundLossUsesTheNewlySelectedForegroundTarget()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        rig.Foreground.CaptureResult = SelectedExternalTarget;
        rig.Window.RaiseApplicationDeactivated();

        rig.Hotkey.Raise(NewSessionTarget);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(2, rig.StateMachine.Current?.Generation);
        Assert.Equal(NewSessionTarget, rig.StateMachine.Current?.Target);
        Assert.Equal(NewSessionTarget, rig.Placement.AppliedTarget);
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
        rig.Window.RaiseRenderingAfterShow = false;
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
    public async Task DwmFenceKeepsWorkspaceCloakedAndUnfocusedUntilContentIsPresented()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Window.RaiseRenderingAfterShow = false;
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;

        rig.Hotkey.Raise(ExternalTarget);
        rig.Window.RaiseActivated();
        rig.Window.RaiseInputAnchorFocused();

        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        Assert.True(rig.Presentation.Cloaked);
        Assert.DoesNotContain("placement.foreground", rig.Log);
        Assert.DoesNotContain("window.activate", rig.Log);
        Assert.DoesNotContain("window.focus", rig.Log);

        rig.Frames.RaiseRendering();

        Assert.Contains("presentation.fence-request", rig.Log);
        Assert.True(rig.Presentation.Cloaked);
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);

        rig.Presentation.CompleteNext();

        Assert.False(rig.Presentation.Cloaked);
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.True(rig.Publisher.Last.IsPresented);
        Assert.True(rig.Publisher.Last.FirstFrameTimestamp > 0);
        AssertOrdered(
            rig.Log,
            "presentation.cloak",
            "window.show",
            "presentation.fence-request",
            "presentation.fence-complete",
            "presentation.uncloak",
            "placement.foreground",
            "window.activate",
            "window.focus");
    }

    [Fact]
    public async Task RenderingCallbackDefersFenceUntilAfterTheRenderDispatcherOperation()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Window.RaiseRenderingAfterShow = false;
        rig.Window.QueueBackgroundPosts = true;
        rig.Hotkey.Raise(ExternalTarget);
        var snapshotsBeforeRendering = rig.Publisher.Snapshots.Count;
        var samplesBeforeRendering = rig.Sampler.CaptureCount;

        rig.Frames.RaiseRendering();

        Assert.DoesNotContain("presentation.fence-request", rig.Log);
        Assert.Equal(snapshotsBeforeRendering, rig.Publisher.Snapshots.Count);
        Assert.Equal(samplesBeforeRendering, rig.Sampler.CaptureCount);

        rig.Window.DrainBackgroundPosts();

        Assert.Contains("presentation.fence-request", rig.Log);
    }

    [Fact]
    public async Task MissingRenderingEventFailsClosedThroughWatchdog()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Window.RaiseRenderingAfterShow = false;

        rig.Hotkey.Raise(ExternalTarget);
        rig.Window.RaiseLastScheduledAction();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.False(rig.Window.IsVisible);
        Assert.False(rig.Presentation.Cloaked);
        Assert.Contains("window.hide", rig.Log);
        Assert.Contains("foreground.restore", rig.Log);
        Assert.Contains("failure watchdog", rig.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WatchdogWinningAgainstPendingFenceCannotDoubleFinishTheGeneration()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Hotkey.Raise(ExternalTarget);

        rig.Window.RaiseLastScheduledAction();
        var hideCalls = rig.Log.Count(entry => entry == "window.hide");
        rig.Presentation.CompleteNext();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.Equal(hideCalls, rig.Log.Count(entry => entry == "window.hide"));
        Assert.DoesNotContain("window.focus", rig.Log);
    }

    [Fact]
    public async Task RejectedFenceRequestFailsClosedWithoutWaitingForWatchdog()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.RejectFenceRequest = true;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.False(rig.Window.IsVisible);
        Assert.False(rig.Presentation.Cloaked);
        Assert.Contains("queue is full", rig.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HideDuringFenceInvalidatesItsLateCompletion()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;
        rig.Hotkey.Raise(ExternalTarget);
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);

        rig.Hotkey.Raise(ExternalTarget);
        var focusCallsAfterHide = rig.Log.Count(entry => entry == "window.focus");
        var uncloakCallsAfterHide = rig.Log.Count(entry => entry == "presentation.uncloak");
        rig.Presentation.CompleteNext();

        Assert.Equal(WorkspaceState.Hidden, rig.StateMachine.State);
        Assert.False(rig.Presentation.Cloaked);
        Assert.Equal(focusCallsAfterHide, rig.Log.Count(entry => entry == "window.focus"));
        Assert.Equal(uncloakCallsAfterHide, rig.Log.Count(entry => entry == "presentation.uncloak"));
    }

    [Fact]
    public async Task FailedHideDuringRevealRestoresTheRevealPipelineInsteadOfClaimingVisible()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Window.RaiseRenderingAfterShow = false;
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;
        rig.Hotkey.Raise(ExternalTarget);
        rig.Window.HideFailuresRemaining = 1;

        rig.Hotkey.Raise(ExternalTarget);

        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        Assert.True(rig.Window.IsVisible);
        Assert.True(rig.Presentation.Cloaked);

        rig.Frames.RaiseRendering();
        rig.Presentation.CompleteNext();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.False(rig.Presentation.Cloaked);
    }

    [Fact]
    public async Task OldFenceCannotUncloakANewerGeneration()
    {
        using var rig = await TestRig.CreateAsync();
        rig.Presentation.AutoComplete = false;
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;
        rig.Hotkey.Raise(ExternalTarget);
        rig.Hotkey.Raise(ExternalTarget);

        rig.Window.RaiseRenderingAfterShow = false;
        rig.Hotkey.Raise(NewSessionTarget);
        var secondGeneration = rig.StateMachine.Current!.Generation;
        Assert.True(rig.Presentation.Cloaked);

        rig.Presentation.CompleteNext();

        Assert.Equal(secondGeneration, rig.StateMachine.Current!.Generation);
        Assert.Equal(WorkspaceState.Revealing, rig.StateMachine.State);
        Assert.True(rig.Presentation.Cloaked);

        rig.Frames.RaiseRendering();
        rig.Presentation.CompleteNext();

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.False(rig.Presentation.Cloaked);
    }

    [Fact]
    public async Task FenceOrUncloakFailureHidesBeforeClearingCloakAndRestoresTarget()
    {
        using var fenceFailureRig = await TestRig.CreateAsync();
        fenceFailureRig.Presentation.AutoComplete = false;
        fenceFailureRig.Hotkey.Raise(ExternalTarget);
        fenceFailureRig.Presentation.CompleteNext(new InvalidOperationException("fence failure"));

        Assert.Equal(WorkspaceState.Hidden, fenceFailureRig.StateMachine.State);
        Assert.False(fenceFailureRig.Presentation.Cloaked);
        AssertOrdered(
            fenceFailureRig.Log,
            "presentation.fence-complete",
            "window.hide",
            "presentation.uncloak",
            "foreground.restore");

        using var uncloakFailureRig = await TestRig.CreateAsync();
        uncloakFailureRig.Presentation.AutoComplete = false;
        uncloakFailureRig.Presentation.UncloakFailuresRemaining = 1;
        uncloakFailureRig.Hotkey.Raise(ExternalTarget);
        uncloakFailureRig.Presentation.CompleteNext();

        Assert.Equal(WorkspaceState.Hidden, uncloakFailureRig.StateMachine.State);
        Assert.False(uncloakFailureRig.Presentation.Cloaked);
        AssertOrdered(
            uncloakFailureRig.Log,
            "presentation.fence-complete",
            "presentation.uncloak",
            "window.hide",
            "foreground.restore");
    }

    [Fact]
    public async Task CancelledWatchdogCannotFinishOrAbortAnAlreadyVisibleGeneration()
    {
        using var rig = await TestRig.CreateVisibleAsync();
        var generation = rig.StateMachine.Current!.Generation;

        rig.Window.RaiseLastScheduledAction(includeCancelled: true);

        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        Assert.Equal(generation, rig.StateMachine.Current!.Generation);
        Assert.DoesNotContain("window.hide", rig.Log);
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

    private static readonly ForegroundTarget SelectedExternalTarget = new(
        0x223,
        556,
        889,
        0x224,
        556,
        889,
        ForegroundWindowRelationship.Descendant);

    private static readonly ForegroundTarget NewSessionTarget = new(
        0x323,
        656,
        989,
        0x324,
        656,
        989,
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
            Window.AfterShow = Frames.RaiseRendering;
            Placement = new FakePlacement(Log, Window);
            Foreground = new FakeForeground(Log);
            Hotkey = new FakeHotkey();
            Publisher = new FakePublisher();
            Presentation = new FakePresentationService(Log);
            Sampler = new FakePerformanceSampler();
            StateMachine = new WorkspaceSessionStateMachine();
            ViewModel = CreateViewModel(Hotkey);
            Controller = new WorkspaceController(
                Window,
                Frames,
                ViewModel,
                StateMachine,
                Hotkey,
                Foreground,
                Placement,
                Sampler,
                Publisher,
                Presentation,
                new WorkspaceHostOptions(false));
        }

        public List<string> Log { get; } = [];
        public FakeWindow Window { get; }
        public FakeFrameSource Frames { get; }
        public FakePlacement Placement { get; }
        public FakeForeground Foreground { get; }
        public FakeHotkey Hotkey { get; }
        public FakePublisher Publisher { get; }
        public FakePresentationService Presentation { get; }
        public FakePerformanceSampler Sampler { get; }
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

        public void Dispose()
        {
            Controller.Dispose();
            Presentation.Dispose();
        }

        private static MainWindowViewModel CreateViewModel(FakeHotkey hotkey)
        {
            var store = new InMemorySettingsStore();
            var appearance = new AppearanceController(
                store,
                new NoOpThemeApplier());
            appearance.InitializeAsync().GetAwaiter().GetResult();
            var hotkeyController = new HotkeyController(store, hotkey);
            hotkeyController.InitializeAsync().GetAwaiter().GetResult();
            return new MainWindowViewModel(
                new SettingsViewModel(appearance, hotkeyController));
        }
    }

    private sealed class FakeWindow(List<string> log) : IWorkspaceWindow
    {
        private EventHandler? _activated;
        private EventHandler? _inputFocused;
        private readonly List<EventHandler> _removedActivated = [];
        private readonly List<EventHandler> _removedInputFocused = [];
        private readonly Queue<Action> _inputPosts = [];
        private readonly Queue<Action> _normalPosts = [];
        private readonly Queue<Action> _backgroundPosts = [];
        private readonly List<FakeScheduledAction> _scheduledActions = [];

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
        public event EventHandler? ApplicationDeactivated;
        public event EventHandler<WorkspaceExternalDragStartedEventArgs>? ExternalDragStarted;
        public event EventHandler<WorkspaceExternalDragEndedEventArgs>? ExternalDragEnded;
        public bool IsVisible { get; private set; }
        public bool HasInputAnchorFocus => InputFocus;
        public bool InputFocus { get; set; }
        public bool FocusSucceeds { get; set; }
        public bool ThrowAfterShow { get; set; }
        public int HideFailuresRemaining { get; set; }
        public bool QueueInputPosts { get; set; }
        public bool QueueNormalPosts { get; set; }
        public bool QueueBackgroundPosts { get; set; }
        public bool RaiseRenderingAfterShow { get; set; } = true;
        public bool RaiseApplicationDeactivatedDuringHide { get; set; }
        public bool TopmostWasAppliedWhenShown { get; private set; }
        public Func<bool>? IsTopmost { get; set; }
        public Action? AfterShow { get; set; }

        public nint EnsureHandle() { log.Add("window.ensure"); return 0x777; }
        public void Show()
        {
            log.Add("window.show");
            IsVisible = true;
            TopmostWasAppliedWhenShown = IsTopmost?.Invoke() == true;
            if (ThrowAfterShow) throw new InvalidOperationException("show failure");
            if (RaiseRenderingAfterShow) AfterShow?.Invoke();
        }

        public void Hide()
        {
            log.Add("window.hide");
            if (HideFailuresRemaining > 0)
            {
                HideFailuresRemaining--;
                throw new InvalidOperationException("hide failure");
            }

            IsVisible = false;
            InputFocus = false;
            if (RaiseApplicationDeactivatedDuringHide)
            {
                ApplicationDeactivated?.Invoke(this, EventArgs.Empty);
            }
        }
        public bool Activate() { log.Add("window.activate"); return true; }
        public bool FocusInputAnchor()
        {
            log.Add("window.focus");
            InputFocus = FocusSucceeds;
            return InputFocus;
        }

        public void PostInput(Action action)
        {
            log.Add("window.post-input");
            if (QueueInputPosts) _inputPosts.Enqueue(action); else action();
        }

        public void PostNormal(Action action)
        {
            log.Add("window.post-normal");
            if (QueueNormalPosts) _normalPosts.Enqueue(action); else action();
        }

        public void PostBackground(Action action)
        {
            log.Add("window.post-background");
            if (QueueBackgroundPosts) _backgroundPosts.Enqueue(action); else action();
        }

        public IDisposable ScheduleNormal(TimeSpan dueTime, Action action)
        {
            log.Add("window.schedule-normal");
            var scheduled = new FakeScheduledAction(action);
            _scheduledActions.Add(scheduled);
            return scheduled;
        }

        public void DrainInputPosts()
        {
            while (_inputPosts.TryDequeue(out var action)) action();
        }

        public void DrainNormalPosts()
        {
            while (_normalPosts.TryDequeue(out var action)) action();
        }

        public void DrainBackgroundPosts()
        {
            while (_backgroundPosts.TryDequeue(out var action)) action();
        }

        public void RaiseLastScheduledAction(bool includeCancelled = false) =>
            _scheduledActions[^1].Raise(includeCancelled);

        public void RaiseActivated() => _activated?.Invoke(this, EventArgs.Empty);
        public void RaiseApplicationDeactivated() =>
            ApplicationDeactivated?.Invoke(this, EventArgs.Empty);
        public void RaiseExternalDragStarted(long dragId) =>
            ExternalDragStarted?.Invoke(this, new WorkspaceExternalDragStartedEventArgs(dragId));
        public void RaiseExternalDragEnded(long dragId, bool dropAccepted) =>
            ExternalDragEnded?.Invoke(
                this,
                new WorkspaceExternalDragEndedEventArgs(dragId, dropAccepted));
        public void RaiseInputAnchorFocused() => _inputFocused?.Invoke(this, EventArgs.Empty);
        public void RaiseFirstRemovedActivation() => _removedActivated[0](this, EventArgs.Empty);
        public void RaiseLastRemovedActivation() => _removedActivated[^1](this, EventArgs.Empty);
        public void RaiseFirstRemovedInputFocus() => _removedInputFocused[0](this, EventArgs.Empty);
        public void RaiseLastRemovedInputFocus() => _removedInputFocused[^1](this, EventArgs.Empty);

        public void RaiseHideRequested(WorkspaceHideReason reason = WorkspaceHideReason.UserAction) =>
            HideRequested?.Invoke(this, new WorkspaceHideRequestedEventArgs(reason));

        private sealed class FakeScheduledAction(Action action) : IDisposable
        {
            private bool _cancelled;

            public void Dispose() => _cancelled = true;

            public void Raise(bool includeCancelled)
            {
                if (includeCancelled || !_cancelled)
                {
                    action();
                }
            }
        }
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

    private sealed class InMemorySettingsStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);

        public Task SaveThemeModeAsync(
            AppThemeMode themeMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveHotkeyGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpThemeApplier : IAppThemeApplier
    {
        public void Apply(AppThemeMode themeMode)
        {
        }
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
        public bool ThrowOnCapture { get; set; }
        public bool ThrowOnRestore { get; set; }
        public int CaptureCallCount { get; private set; }
        public int RestoreCallCount { get; private set; }
        public ForegroundTarget Capture()
        {
            log.Add("foreground.capture");
            CaptureCallCount++;
            if (ThrowOnCapture) throw new InvalidOperationException("capture failure");
            return CaptureResult;
        }
        public ForegroundRestoreResult Restore(ForegroundTarget target)
        {
            log.Add("foreground.restore");
            RestoreCallCount++;
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

    private sealed class FakeHotkey : IConfigurableGlobalHotkeyService
    {
        private long _nextPressGeneration;

        public event EventHandler<HotkeyPressEvent>? PressChanged;
        public event EventHandler<Exception>? Faulted;
        public GlobalHotkeyGesture CurrentGesture { get; private set; } =
            GlobalHotkeyGesture.Default;
        public string DisplayGesture => CurrentGesture.DisplayName;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SuspendAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ChangeGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default)
        {
            CurrentGesture = gesture;
            return Task.CompletedTask;
        }
        public void Raise(ForegroundTarget target)
        {
            var generation = RaiseStarted(target);
            RaiseReleased(generation, TimeSpan.FromMilliseconds(100));
        }
        public long RaiseStarted(ForegroundTarget target)
        {
            var generation = checked(++_nextPressGeneration);
            PressChanged?.Invoke(this, new HotkeyPressStarted(
                generation,
                target,
                Stopwatch.GetTimestamp()));
            return generation;
        }
        public void RaiseReleased(long generation, TimeSpan heldDuration) =>
            PressChanged?.Invoke(this, new HotkeyPressReleased(
                generation,
                heldDuration,
                Stopwatch.GetTimestamp()));
        public void RaiseCanceled(long generation) =>
            PressChanged?.Invoke(this, new HotkeyPressCanceled(
                generation,
                Stopwatch.GetTimestamp()));
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

    private sealed class FakePresentationService(List<string> log)
        : IWorkspacePresentationService
    {
        private readonly Queue<long> _requests = [];

        public event EventHandler<WorkspacePresentationFenceCompletedEventArgs>? FenceCompleted;

        public bool AutoComplete { get; set; } = true;
        public bool Cloaked { get; private set; }
        public bool RejectFenceRequest { get; set; }
        public int UncloakFailuresRemaining { get; set; }

        public void SetCloaked(nint workspaceWindow, bool cloaked)
        {
            log.Add(cloaked ? "presentation.cloak" : "presentation.uncloak");
            if (!cloaked && UncloakFailuresRemaining > 0)
            {
                UncloakFailuresRemaining--;
                throw new InvalidOperationException("uncloak failure");
            }

            Cloaked = cloaked;
        }

        public bool IsCloaked(nint workspaceWindow) => Cloaked;

        public bool TryRequestFence(long generation)
        {
            log.Add("presentation.fence-request");
            if (RejectFenceRequest)
            {
                return false;
            }

            _requests.Enqueue(generation);
            if (AutoComplete)
            {
                CompleteNext();
            }

            return true;
        }

        public void CompleteNext(Exception? exception = null)
        {
            var generation = _requests.Dequeue();
            log.Add("presentation.fence-complete");
            FenceCompleted?.Invoke(
                this,
                new WorkspacePresentationFenceCompletedEventArgs(generation, exception));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePerformanceSampler : IPerformanceSampler
    {
        public int CaptureCount { get; private set; }

        public PerformanceSnapshot Capture()
        {
            CaptureCount++;
            return new PerformanceSnapshot(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0);
        }
    }
}
