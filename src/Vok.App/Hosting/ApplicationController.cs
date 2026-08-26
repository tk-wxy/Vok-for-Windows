using Vok.Core.Platform;

namespace Vok.Hosting;

public sealed class ApplicationController : IDisposable
{
    private readonly ITrayIconService _trayIconService;
    private readonly IWorkspaceCommands _workspace;
    private readonly IApplicationLifetime _applicationLifetime;
    private bool _started;
    private bool _disposed;
    private bool _shutdownStarted;

    public ApplicationController(
        ITrayIconService trayIconService,
        IWorkspaceCommands workspace,
        IApplicationLifetime applicationLifetime)
    {
        _trayIconService = trayIconService;
        _workspace = workspace;
        _applicationLifetime = applicationLifetime;

        _trayIconService.CommandInvoked += OnTrayCommandInvoked;
        _trayIconService.Faulted += OnTrayFaulted;
        _workspace.ShutdownRequested += OnWorkspaceShutdownRequested;
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _trayIconService.Start();
        }
        catch (Exception exception)
        {
            _workspace.ReportFault(exception);
        }

        await _workspace.StartAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIconService.CommandInvoked -= OnTrayCommandInvoked;
        _trayIconService.Faulted -= OnTrayFaulted;
        _workspace.ShutdownRequested -= OnWorkspaceShutdownRequested;
        _trayIconService.Dispose();
    }

    private void OnTrayCommandInvoked(object? sender, TrayIconCommandEventArgs e)
    {
        if (_disposed || _shutdownStarted)
        {
            return;
        }

        switch (e.Command)
        {
            case TrayIconCommand.ShowWorkspace:
                _workspace.EnsureVisible(e.ForegroundTarget, e.NativeTimestamp);
                break;
            case TrayIconCommand.OpenSettings:
                _workspace.EnsureVisible(
                    e.ForegroundTarget,
                    e.NativeTimestamp,
                    WorkspaceDestination.Settings);
                break;
            case TrayIconCommand.ExitApplication:
                _workspace.RequestExit();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.Command, null);
        }
    }

    private void OnTrayFaulted(object? sender, Exception exception)
    {
        if (!_disposed)
        {
            _workspace.ReportFault(exception);
        }
    }

    private void OnWorkspaceShutdownRequested(object? sender, EventArgs e)
    {
        if (_disposed || _shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            _applicationLifetime.Shutdown();
        }
        catch
        {
            _shutdownStarted = false;
            throw;
        }
    }
}
