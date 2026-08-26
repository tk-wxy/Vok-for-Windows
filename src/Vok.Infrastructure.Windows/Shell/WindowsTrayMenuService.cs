using System.Runtime.ExceptionServices;
using Vok.Core.Platform;

namespace Vok.Infrastructure.Windows.Shell;

public sealed class WindowsTrayMenuService : ITrayMenuService
{
    internal const uint ShowWorkspaceCommandId = 1;
    internal const uint OpenSettingsCommandId = 2;
    internal const uint ExitApplicationCommandId = 3;

    private readonly IWindowsTrayMenuNativeMethods _nativeMethods;

    public WindowsTrayMenuService()
        : this(new WindowsTrayMenuNativeMethods())
    {
    }

    internal WindowsTrayMenuService(IWindowsTrayMenuNativeMethods nativeMethods)
    {
        _nativeMethods = nativeMethods;
    }

    public TrayIconCommand? Show(
        nint ownerWindow,
        Guid iconId,
        bool keyboardInvocation)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("The tray owner window is unavailable.", nameof(ownerWindow));
        }

        var menu = _nativeMethods.CreatePopupMenu();
        TrayIconCommand? command = null;
        Exception? operationFailure = null;
        var trackingStarted = false;
        try
        {
            BuildMenu(menu);
            var anchor = keyboardInvocation &&
                         _nativeMethods.TryGetIconAnchor(ownerWindow, iconId, out var iconAnchor)
                ? iconAnchor
                : _nativeMethods.GetCursorPosition();
            _nativeMethods.SetForegroundWindow(ownerWindow);
            trackingStarted = true;
            command = MapCommand(
                _nativeMethods.TrackPopupMenu(menu, ownerWindow, anchor));
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        var cleanupFailures = Cleanup(menu, ownerWindow, trackingStarted);
        ThrowFailures(operationFailure, cleanupFailures);
        return command;
    }

    private void BuildMenu(nint menu)
    {
        _nativeMethods.AppendMenuItem(
            menu,
            ShowWorkspaceCommandId,
            "显示界面(&S)");
        _nativeMethods.AppendMenuItem(
            menu,
            OpenSettingsCommandId,
            "设置(&O)");
        _nativeMethods.AppendSeparator(menu);
        _nativeMethods.AppendMenuItem(
            menu,
            ExitApplicationCommandId,
            "退出(&X)");
        _nativeMethods.SetDefaultMenuItem(menu, ShowWorkspaceCommandId);
    }

    private List<Exception>? Cleanup(
        nint menu,
        nint ownerWindow,
        bool trackingStarted)
    {
        List<Exception>? failures = null;
        if (trackingStarted)
        {
            try
            {
                _nativeMethods.PostMenuDismissal(ownerWindow);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            _nativeMethods.DestroyMenu(menu);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        return failures;
    }

    private static TrayIconCommand? MapCommand(uint commandId) => commandId switch
    {
        0 => null,
        ShowWorkspaceCommandId => TrayIconCommand.ShowWorkspace,
        OpenSettingsCommandId => TrayIconCommand.OpenSettings,
        ExitApplicationCommandId => TrayIconCommand.ExitApplication,
        _ => throw new InvalidOperationException(
            $"The native tray menu returned unknown command {commandId}."),
    };

    private static void ThrowFailures(
        Exception? operationFailure,
        List<Exception>? cleanupFailures)
    {
        if (operationFailure is null && cleanupFailures is null)
        {
            return;
        }

        if (operationFailure is not null && cleanupFailures is null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (operationFailure is null && cleanupFailures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        var failures = new List<Exception>();
        if (operationFailure is not null)
        {
            failures.Add(operationFailure);
        }

        if (cleanupFailures is not null)
        {
            failures.AddRange(cleanupFailures);
        }

        throw new AggregateException("The native tray menu operation failed.", failures);
    }
}
