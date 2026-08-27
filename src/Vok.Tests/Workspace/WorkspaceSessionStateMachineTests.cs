using Vok.Core.Workspace;
using Xunit;

namespace Vok.Tests.Workspace;

public sealed class WorkspaceSessionStateMachineTests
{
    private static readonly ForegroundTarget Target = new(42, 7, 9);
    private static readonly WorkspacePresentationStatus Ready = new(true, true, true, true, true, true);

    [Fact]
    public void StartsHiddenWithoutSession()
    {
        var machine = new WorkspaceSessionStateMachine();

        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
    }

    [Theory]
    [InlineData(WorkspaceHideReason.Hotkey)]
    [InlineData(WorkspaceHideReason.Escape)]
    [InlineData(WorkspaceHideReason.UserAction)]
    [InlineData(WorkspaceHideReason.ApplicationExit)]
    [InlineData(WorkspaceHideReason.ShowFailure)]
    public void ExplicitHidePoliciesRestoreTheCapturedTarget(WorkspaceHideReason reason)
    {
        var policy = WorkspaceHidePolicy.For(reason);

        Assert.Equal(reason, policy.Reason);
        Assert.Equal(
            WorkspaceTargetRestoreMode.RestoreCapturedTarget,
            policy.TargetRestoreMode);
    }

    [Fact]
    public void ForegroundLossPolicyPreservesTheCurrentForeground()
    {
        var policy = WorkspaceHidePolicy.For(WorkspaceHideReason.ForegroundLost);

        Assert.Equal(WorkspaceHideReason.ForegroundLost, policy.Reason);
        Assert.Equal(
            WorkspaceTargetRestoreMode.PreserveCurrentForeground,
            policy.TargetRestoreMode);
    }

    [Fact]
    public void ShowAndHideCompleteOneOwnedGeneration()
    {
        var machine = new WorkspaceSessionStateMachine();

        var show = machine.BeginShow(Target, 100);
        Assert.True(show.Accepted);
        Assert.Equal(WorkspaceState.Revealing, machine.State);
        Assert.True(machine.CompleteShow(show.Generation, Ready));
        Assert.Equal(WorkspaceState.Visible, machine.State);

        var hide = machine.BeginHide(WorkspaceHideReason.Hotkey);
        Assert.True(hide.Accepted);
        Assert.Equal(show.Generation, hide.Generation);
        Assert.True(machine.CompleteHide(hide.Generation));
        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
    }

    [Fact]
    public void DuplicateShowIsRejectedBeforeSideEffects()
    {
        var machine = new WorkspaceSessionStateMachine();
        var first = machine.BeginShow(Target, 100);

        var duplicate = machine.BeginShow(Target, 101);

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        Assert.Equal(first.Generation, duplicate.Generation);
        Assert.Equal(WorkspaceState.Revealing, machine.State);
    }

    [Theory]
    [InlineData(false, true, true, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, true, false, true, true, true)]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(true, true, true, true, true, false)]
    public void ShowCannotCompleteUntilEveryPresentationGateIsObserved(
        bool visible,
        bool topmost,
        bool foreground,
        bool win32Focus,
        bool wpfFocus,
        bool presented)
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);
        var incomplete = new WorkspacePresentationStatus(
            visible,
            topmost,
            foreground,
            win32Focus,
            wpfFocus,
            presented);

        Assert.False(machine.CompleteShow(show.Generation, incomplete));
        Assert.Equal(WorkspaceState.Revealing, machine.State);
        Assert.Equal(show.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void HideDuringRevealRejectsLateShowCompletion()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);

        var hide = machine.BeginHide(WorkspaceHideReason.Hotkey);

        Assert.True(hide.Accepted);
        Assert.False(machine.CompleteShow(show.Generation, Ready));
        Assert.Equal(WorkspaceState.Hiding, machine.State);
        Assert.True(machine.CompleteHide(hide.Generation));
    }

    [Fact]
    public void LateHideCompletionCannotClearNewSession()
    {
        var machine = new WorkspaceSessionStateMachine();
        var firstShow = machine.BeginShow(Target, 100);
        Assert.True(machine.CompleteShow(firstShow.Generation, Ready));
        var firstHide = machine.BeginHide(WorkspaceHideReason.Escape);
        Assert.True(machine.CompleteHide(firstHide.Generation));

        var secondShow = machine.BeginShow(new ForegroundTarget(84, 8, 10), 200);
        Assert.True(machine.CompleteShow(secondShow.Generation, Ready));

        Assert.False(machine.CompleteHide(firstHide.Generation));
        Assert.Equal(WorkspaceState.Visible, machine.State);
        Assert.Equal(secondShow.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void CompletionCanOnlyBeClaimedOnce()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);

        Assert.True(machine.CompleteShow(show.Generation, Ready));
        Assert.False(machine.CompleteShow(show.Generation, Ready));

        var hide = machine.BeginHide(WorkspaceHideReason.UserAction);
        Assert.True(machine.CompleteHide(hide.Generation));
        Assert.False(machine.CompleteHide(hide.Generation));
    }

    [Fact]
    public void ShowFailureReturnsToCleanHiddenState()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);

        Assert.True(machine.FailShow(show.Generation));
        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
        Assert.False(machine.FailShow(show.Generation));
    }

    [Fact]
    public void FailureAfterShowCompletionReturnsToCleanHiddenState()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);
        Assert.True(machine.CompleteShow(show.Generation, Ready));

        Assert.True(machine.FailShow(show.Generation));

        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
        Assert.False(machine.FailShow(show.Generation));
    }

    [Fact]
    public void LateShowFailureCannotClearNewSession()
    {
        var machine = new WorkspaceSessionStateMachine();
        var firstShow = machine.BeginShow(Target, 100);
        Assert.True(machine.FailShow(firstShow.Generation));
        var secondShow = machine.BeginShow(Target, 200);
        Assert.True(machine.CompleteShow(secondShow.Generation, Ready));

        Assert.False(machine.FailShow(firstShow.Generation));

        Assert.Equal(WorkspaceState.Visible, machine.State);
        Assert.Equal(secondShow.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void ShowFailureCannotOverrideAnOwnedHide()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);
        Assert.True(machine.CompleteShow(show.Generation, Ready));
        var hide = machine.BeginHide(WorkspaceHideReason.Hotkey);

        Assert.False(machine.FailShow(show.Generation));

        Assert.Equal(WorkspaceState.Hiding, machine.State);
        Assert.Equal(hide.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void HideFailureRollsBackToVisibleState()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);
        Assert.True(machine.CompleteShow(show.Generation, Ready));
        var hide = machine.BeginHide(WorkspaceHideReason.UserAction);

        Assert.True(machine.RollBackHide(hide.Generation));
        Assert.Equal(WorkspaceState.Visible, machine.State);
        Assert.Equal(show.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void HideFailureDuringRevealRollsBackToRevealingState()
    {
        var machine = new WorkspaceSessionStateMachine();
        var show = machine.BeginShow(Target, 100);
        var hide = machine.BeginHide(WorkspaceHideReason.UserAction);

        Assert.True(machine.RollBackHide(hide.Generation));
        Assert.Equal(WorkspaceState.Revealing, machine.State);
        Assert.Equal(show.Generation, machine.Current?.Generation);
    }

    [Fact]
    public void HiddenStateRejectsHideWithoutCreatingSession()
    {
        var machine = new WorkspaceSessionStateMachine();

        var hide = machine.BeginHide(WorkspaceHideReason.Escape);

        Assert.False(hide.Accepted);
        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
    }

    [Fact]
    public void ThousandCyclesLeaveNoSessionAndUseMonotonicGenerations()
    {
        var machine = new WorkspaceSessionStateMachine();
        long previousGeneration = 0;

        for (var index = 0; index < 1_000; index++)
        {
            var show = machine.BeginShow(Target, index);
            Assert.True(show.Generation > previousGeneration);
            Assert.True(machine.CompleteShow(show.Generation, Ready));
            var hide = machine.BeginHide(WorkspaceHideReason.Hotkey);
            Assert.True(machine.CompleteHide(hide.Generation));
            previousGeneration = show.Generation;
        }

        Assert.Equal(WorkspaceState.Hidden, machine.State);
        Assert.Null(machine.Current);
    }
}
