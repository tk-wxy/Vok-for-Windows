using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed class ApplicationControllerTests
{
    private static readonly ForegroundTarget ExternalTarget = new(0x123, 42, 84);

    [Fact]
    public async Task StartsTrayBeforeWorkspace()
    {
        var log = new List<string>();
        var tray = new FakeTrayIconService(log);
        var workspace = new FakeWorkspaceCommands(log);
        var lifetime = new FakeApplicationLifetime();
        using var controller = new ApplicationController(tray, workspace, lifetime);

        await controller.StartAsync();
        await controller.StartAsync();

        Assert.Equal(["tray.start", "workspace.start"], log);
    }

    [Fact]
    public async Task TrayStartupFailureIsReportedWithoutBlockingWorkspaceStartup()
    {
        var log = new List<string>();
        var failure = new InvalidOperationException("tray failure");
        var tray = new FakeTrayIconService(log) { StartException = failure };
        var workspace = new FakeWorkspaceCommands(log);
        using var controller = new ApplicationController(
            tray,
            workspace,
            new FakeApplicationLifetime());

        await controller.StartAsync();

        Assert.Same(failure, Assert.Single(workspace.Faults));
        Assert.Equal(["tray.start", "workspace.fault", "workspace.start"], log);
    }

    [Fact]
    public void TrayCommandsUseIdempotentWorkspaceCommands()
    {
        var log = new List<string>();
        var tray = new FakeTrayIconService(log);
        var workspace = new FakeWorkspaceCommands(log);
        using var controller = new ApplicationController(
            tray,
            workspace,
            new FakeApplicationLifetime());

        tray.RaiseCommand(TrayIconCommand.ShowWorkspace, ExternalTarget, 101);
        tray.RaiseCommand(TrayIconCommand.OpenSettings, ExternalTarget, 102);
        tray.RaiseCommand(TrayIconCommand.ExitApplication, ExternalTarget, 103);

        Assert.Collection(
            workspace.ShowRequests,
            request =>
            {
                Assert.Equal(ExternalTarget, request.Target);
                Assert.Equal(101, request.Timestamp);
                Assert.Equal(WorkspaceDestination.PreserveCurrent, request.Destination);
            },
            request =>
            {
                Assert.Equal(ExternalTarget, request.Target);
                Assert.Equal(102, request.Timestamp);
                Assert.Equal(WorkspaceDestination.Settings, request.Destination);
            });
        Assert.Equal(1, workspace.ExitRequestCount);
    }

    [Fact]
    public void WorkspaceShutdownIsForwardedOnlyOnce()
    {
        var log = new List<string>();
        var tray = new FakeTrayIconService(log);
        var workspace = new FakeWorkspaceCommands(log);
        var lifetime = new FakeApplicationLifetime();
        using var controller = new ApplicationController(tray, workspace, lifetime);

        workspace.RaiseShutdownRequested();
        workspace.RaiseShutdownRequested();
        tray.RaiseCommand(TrayIconCommand.ShowWorkspace, ExternalTarget, 104);

        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Empty(workspace.ShowRequests);
    }

    [Fact]
    public void FailedLifetimeShutdownCanBeRetried()
    {
        var log = new List<string>();
        var tray = new FakeTrayIconService(log);
        var workspace = new FakeWorkspaceCommands(log);
        var lifetime = new FakeApplicationLifetime { ThrowOnShutdown = true };
        using var controller = new ApplicationController(tray, workspace, lifetime);

        Assert.Throws<InvalidOperationException>(workspace.RaiseShutdownRequested);
        lifetime.ThrowOnShutdown = false;
        workspace.RaiseShutdownRequested();

        Assert.Equal(2, lifetime.ShutdownCount);
    }

    [Fact]
    public void DisposeRemovesSubscriptionsAndDisposesTray()
    {
        var log = new List<string>();
        var tray = new FakeTrayIconService(log);
        var workspace = new FakeWorkspaceCommands(log);
        var lifetime = new FakeApplicationLifetime();
        var controller = new ApplicationController(tray, workspace, lifetime);

        controller.Dispose();
        tray.RaiseCommand(TrayIconCommand.ShowWorkspace, ExternalTarget, 105);
        workspace.RaiseShutdownRequested();

        Assert.Equal(1, tray.DisposeCount);
        Assert.Empty(workspace.ShowRequests);
        Assert.Equal(0, lifetime.ShutdownCount);
    }

    private sealed class FakeTrayIconService(List<string> log) : ITrayIconService
    {
        public event EventHandler<TrayIconCommandEventArgs>? CommandInvoked;
        public event EventHandler<Exception>? Faulted;
        public Exception? StartException { get; init; }
        public int DisposeCount { get; private set; }

        public void Start()
        {
            log.Add("tray.start");
            if (StartException is not null)
            {
                throw StartException;
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            log.Add("tray.dispose");
        }

        public void RaiseCommand(
            TrayIconCommand command,
            ForegroundTarget target,
            long timestamp) =>
            CommandInvoked?.Invoke(
                this,
                new TrayIconCommandEventArgs(command, target, timestamp));

        public void RaiseFault(Exception exception) => Faulted?.Invoke(this, exception);
    }

    private sealed class FakeWorkspaceCommands(List<string> log) : IWorkspaceCommands
    {
        public event EventHandler? ShutdownRequested;
        public List<(
            ForegroundTarget Target,
            long Timestamp,
            WorkspaceDestination Destination)>
            ShowRequests
        { get; } = [];
        public List<Exception> Faults { get; } = [];
        public int ExitRequestCount { get; private set; }

        public Task StartAsync()
        {
            log.Add("workspace.start");
            return Task.CompletedTask;
        }

        public Task<bool> HideForLauncherActionAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void EnsureVisible(
            ForegroundTarget foregroundTarget,
            long requestedTimestamp,
            WorkspaceDestination destination = WorkspaceDestination.PreserveCurrent)
        {
            ShowRequests.Add((foregroundTarget, requestedTimestamp, destination));
            log.Add("workspace.show");
        }

        public void RequestExit()
        {
            ExitRequestCount++;
            log.Add("workspace.exit");
        }

        public void ReportFault(Exception exception)
        {
            Faults.Add(exception);
            log.Add("workspace.fault");
        }

        public void RaiseShutdownRequested() =>
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeApplicationLifetime : IApplicationLifetime
    {
        public int ShutdownCount { get; private set; }
        public bool ThrowOnShutdown { get; set; }

        public void Shutdown()
        {
            ShutdownCount++;
            if (ThrowOnShutdown)
            {
                throw new InvalidOperationException("shutdown failure");
            }
        }
    }
}
