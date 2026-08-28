using Vok.Core.Clipboard;
using Xunit;

namespace Vok.Tests.Clipboard;

public sealed class ClipboardSequenceCoordinatorTests
{
    [Fact]
    public void CurrentSequenceAtInitializationIsNotCaptured()
    {
        var coordinator = new ClipboardSequenceCoordinator();
        coordinator.Initialize(41);

        var notification = coordinator.Notify(41);

        Assert.True(notification.IsDuplicate);
        Assert.False(notification.ShouldScheduleCapture);
        Assert.False(coordinator.GetState().HasActive);
    }

    [Fact]
    public void RepeatedNotificationSchedulesOnlyOneCapture()
    {
        var coordinator = CreateInitialized();

        var first = coordinator.Notify(11);
        var duplicate = coordinator.Notify(11);

        Assert.True(first.ShouldScheduleCapture);
        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.False(duplicate.ShouldScheduleCapture);
        Assert.Equal(11u, coordinator.GetState().ActiveSequence);
    }

    [Fact]
    public void LatestPendingSequenceReplacesEarlierPendingWithoutNumericOrdering()
    {
        var coordinator = CreateInitialized();
        _ = coordinator.Notify(100);

        var queued = coordinator.Notify(uint.MaxValue);
        var replacedByWrappedSequence = coordinator.Notify(1);

        Assert.False(queued.ShouldScheduleCapture);
        Assert.False(queued.ReplacedPending);
        Assert.True(replacedByWrappedSequence.ReplacedPending);
        var state = coordinator.GetState();
        Assert.Equal(100u, state.ActiveSequence);
        Assert.Equal(1u, state.PendingSequence);
    }

    [Fact]
    public void CapturingCurrentClipboardAlsoCompletesMatchingPendingSignal()
    {
        var coordinator = CreateInitialized();
        _ = coordinator.Notify(20);
        _ = coordinator.Notify(21);

        var completion = coordinator.Complete(21);

        Assert.False(completion.ShouldScheduleCapture);
        var state = coordinator.GetState();
        Assert.False(state.HasActive);
        Assert.False(state.HasPending);
        Assert.Equal(21u, state.CommittedSequence);
    }

    [Fact]
    public void DifferentPendingSequenceBecomesNextOwnedCapture()
    {
        var coordinator = CreateInitialized();
        _ = coordinator.Notify(20);
        _ = coordinator.Notify(21);

        var completion = coordinator.Complete(20);

        Assert.True(completion.ShouldScheduleCapture);
        Assert.Equal(21u, completion.Sequence);
        var state = coordinator.GetState();
        Assert.True(state.HasActive);
        Assert.False(state.HasPending);
        Assert.Equal(21u, state.ActiveSequence);
    }

    [Fact]
    public void CoordinatorRejectsInvalidLifecycleCalls()
    {
        var coordinator = new ClipboardSequenceCoordinator();

        Assert.Throws<InvalidOperationException>(() => coordinator.Notify(1));
        coordinator.Initialize(0);
        Assert.Throws<InvalidOperationException>(() => coordinator.Initialize(0));
        Assert.Throws<InvalidOperationException>(() => coordinator.Complete(0));
    }

    private static ClipboardSequenceCoordinator CreateInitialized()
    {
        var coordinator = new ClipboardSequenceCoordinator();
        coordinator.Initialize(10);
        return coordinator;
    }
}
