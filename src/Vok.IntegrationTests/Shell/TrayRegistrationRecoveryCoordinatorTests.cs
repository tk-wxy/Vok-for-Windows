using Vok.Core.Platform;
using Vok.Features.Tray;
using Xunit;

namespace Vok.IntegrationTests.Shell;

public sealed class TrayRegistrationRecoveryCoordinatorTests
{
    private static readonly nint OwnerWindow = (nint)0x1234;
    private static readonly Guid IconId = Guid.Parse(
        "730b6bcc-1c23-43a5-94d7-7212992e6021");

    [Fact]
    public void HealthyStartupSchedulesLowFrequencyWatchdog()
    {
        var probe = new FakeProbe { IsRegistered = true };
        var scheduler = new FakeScheduler();
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () => throw new InvalidOperationException("not expected"));

        coordinator.Start();

        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.Delay);
    }

    [Fact]
    public void EnvironmentSignalRepairsMissingRegistrationAndVerifiesPostcondition()
    {
        var probe = new FakeProbe { IsRegistered = true };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () =>
            {
                repairCount++;
                probe.IsRegistered = true;
            });
        coordinator.Start();
        probe.IsRegistered = false;

        coordinator.RequestCheck(TrayRegistrationRecoveryTrigger.PowerResume);

        Assert.Equal(1, repairCount);
        Assert.Equal(3, probe.ProbeCount);
        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
        Assert.Equal(1, coordinator.RepairAttempts);
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.Delay);
    }

    [Fact]
    public void WatchdogConfirmsMissBeforeRepairing()
    {
        var probe = new FakeProbe { IsRegistered = true };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () =>
            {
                repairCount++;
                probe.IsRegistered = true;
            });
        coordinator.Start();
        probe.IsRegistered = false;

        scheduler.Fire();

        Assert.Equal(TrayRegistrationRecoveryState.Suspect, coordinator.State);
        Assert.Equal(0, repairCount);
        Assert.Equal(TimeSpan.FromMilliseconds(250), scheduler.Delay);

        scheduler.Fire();

        Assert.Equal(1, repairCount);
        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
    }

    [Fact]
    public void ExhaustedEpisodeReportsOnceAndReturnsToWatchdog()
    {
        var probe = new FakeProbe { IsRegistered = false };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        var faults = new List<Exception>();
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () => repairCount++,
            reportFault: faults.Add,
            retryDelays:
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
            ]);

        coordinator.Start();
        Assert.Equal(TimeSpan.FromMilliseconds(10), scheduler.Delay);
        scheduler.Fire();
        Assert.Equal(TimeSpan.FromMilliseconds(20), scheduler.Delay);
        scheduler.Fire();

        Assert.Equal(3, repairCount);
        Assert.Equal(TrayRegistrationRecoveryState.Degraded, coordinator.State);
        var fault = Assert.IsType<TrayRegistrationRecoveryException>(
            Assert.Single(faults));
        Assert.Equal(3, fault.Attempts);
        Assert.Equal(TrayRegistrationRecoveryTrigger.Startup, fault.Trigger);
        Assert.Equal(FakeProbe.MissingResult, fault.NativeResult);
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.Delay);
    }

    [Fact]
    public void NewSignalInvalidatesAStaleRetryCallback()
    {
        var probe = new FakeProbe { IsRegistered = false };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () => repairCount++,
            retryDelays: [TimeSpan.FromMilliseconds(10)]);
        coordinator.Start();
        var staleRetry = scheduler.Callback;
        Assert.NotNull(staleRetry);
        probe.IsRegistered = true;

        coordinator.RequestCheck(TrayRegistrationRecoveryTrigger.SessionUnlock);
        staleRetry();

        Assert.Equal(1, repairCount);
        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.Delay);
    }

    [Fact]
    public void OpenMenuDefersRecoveryUntilUnblocked()
    {
        var probe = new FakeProbe { IsRegistered = true };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () =>
            {
                repairCount++;
                probe.IsRegistered = true;
            });
        coordinator.Start();
        probe.IsRegistered = false;
        coordinator.SetBlocked(true);

        coordinator.RequestCheck(TrayRegistrationRecoveryTrigger.TaskbarCreated);

        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(0, repairCount);

        coordinator.SetBlocked(false);

        Assert.Equal(1, repairCount);
        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
    }

    [Fact]
    public void DisposedCoordinatorRejectsLateScheduledCallback()
    {
        var probe = new FakeProbe { IsRegistered = true };
        var scheduler = new FakeScheduler();
        var repairCount = 0;
        var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () => repairCount++);
        coordinator.Start();
        var staleWatchdog = scheduler.Callback;
        Assert.NotNull(staleWatchdog);

        coordinator.Dispose();
        probe.IsRegistered = false;
        staleWatchdog();

        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(0, repairCount);
        Assert.Equal(TrayRegistrationRecoveryState.Disposed, coordinator.State);
    }

    [Fact]
    public void ReentrantSignalDoesNotDuplicateRepair()
    {
        var probe = new FakeProbe { IsRegistered = false };
        var scheduler = new FakeScheduler();
        TrayRegistrationRecoveryCoordinator? coordinator = null;
        var repairCount = 0;
        coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () =>
            {
                repairCount++;
                probe.IsRegistered = true;
                coordinator!.RequestCheck(
                    TrayRegistrationRecoveryTrigger.TaskbarCreated);
            });
        using (coordinator)
        {
            coordinator.Start();

            Assert.Equal(1, repairCount);
            Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
        }
    }

    [Fact]
    public void ShellPostconditionWinsWhenRepairThrowsAfterRegistering()
    {
        var probe = new FakeProbe { IsRegistered = false };
        var scheduler = new FakeScheduler();
        var faults = new List<Exception>();
        using var coordinator = CreateCoordinator(
            probe,
            scheduler,
            repair: () =>
            {
                probe.IsRegistered = true;
                throw new InvalidOperationException("late repair failure");
            },
            reportFault: faults.Add);

        coordinator.Start();

        Assert.Empty(faults);
        Assert.Equal(TrayRegistrationRecoveryState.Healthy, coordinator.State);
        Assert.Equal(1, coordinator.RepairAttempts);
    }

    private static TrayRegistrationRecoveryCoordinator CreateCoordinator(
        FakeProbe probe,
        FakeScheduler scheduler,
        Action repair,
        Action<Exception>? reportFault = null,
        IReadOnlyList<TimeSpan>? retryDelays = null) =>
        new(
            probe,
            OwnerWindow,
            IconId,
            repair,
            scheduler,
            new TrayRegistrationRecoveryOptions(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMilliseconds(250),
                retryDelays ?? [TimeSpan.FromMilliseconds(10)]),
            reportFault ?? (_ => { }));

    private sealed class FakeProbe : ITrayRegistrationProbe
    {
        public const int MissingResult = unchecked((int)0x80004005);

        public bool IsRegistered { get; set; }

        public int ProbeCount { get; private set; }

        public TrayRegistrationProbeResult Probe(nint ownerWindow, Guid iconId)
        {
            Assert.Equal(OwnerWindow, ownerWindow);
            Assert.Equal(IconId, iconId);
            ProbeCount++;
            return new TrayRegistrationProbeResult(
                IsRegistered,
                IsRegistered ? 0 : MissingResult);
        }
    }

    private sealed class FakeScheduler : ITrayRecoveryScheduler
    {
        public TimeSpan? Delay { get; private set; }

        public Action? Callback { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            Delay = delay;
            Callback = callback;
        }

        public void Cancel()
        {
            Delay = null;
            Callback = null;
        }

        public void Dispose() => Cancel();

        public void Fire()
        {
            var callback = Callback ?? throw new InvalidOperationException(
                "No callback is scheduled.");
            Callback = null;
            Delay = null;
            callback();
        }
    }
}
