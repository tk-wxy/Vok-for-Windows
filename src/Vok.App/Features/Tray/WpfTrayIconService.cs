using System.Diagnostics;
using System.Drawing;
using System.Windows;
using H.NotifyIcon;
using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Features.Tray;

public sealed class WpfTrayIconService : ITrayIconService
{
    private readonly IForegroundWindowService _foregroundWindowService;
    private readonly ITrayMenuService _trayMenuService;
    private TaskbarIcon? _taskbarIcon;
    private Icon? _icon;
    private ForegroundTarget _contextForegroundTarget;
    private long _contextRequestedTimestamp;
    private bool _contextMenuOpen;
    private int _ownerThreadId;
    private int _lifecycle;

    public WpfTrayIconService(
        IForegroundWindowService foregroundWindowService,
        ITrayMenuService trayMenuService)
    {
        _foregroundWindowService = foregroundWindowService;
        _trayMenuService = trayMenuService;
    }

    public event EventHandler<TrayIconCommandEventArgs>? CommandInvoked;

    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous == 1)
        {
            return;
        }

        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(WpfTrayIconService));
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        if (Thread.CurrentThread.GetApartmentState() is not ApartmentState.STA)
        {
            _ownerThreadId = 0;
            Interlocked.Exchange(ref _lifecycle, 0);
            throw new InvalidOperationException(
                "The tray icon must be started on the WPF UI STA thread.");
        }

        if (Application.Current is { } application &&
            !application.Dispatcher.CheckAccess())
        {
            _ownerThreadId = 0;
            Interlocked.Exchange(ref _lifecycle, 0);
            throw new InvalidOperationException(
                "The tray icon must be started on the WPF application dispatcher.");
        }

        try
        {
            _icon = LoadProcessIcon();
            _taskbarIcon = new TaskbarIcon
            {
                CustomName = "Vok.Workspace",
                Icon = _icon,
                NoLeftClickDelay = true,
                ToolTipText = "Vok",
            };
            _taskbarIcon.TrayLeftMouseUp += OnTrayLeftMouseUp;
            _taskbarIcon.TrayRightMouseDown += OnTrayRightMouseDown;
            _taskbarIcon.TrayRightMouseUp += OnTrayRightMouseUp;
            _taskbarIcon.TrayKeyboardContextMenu += OnTrayKeyboardContextMenu;
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch
        {
            DisposeResources();
            _ownerThreadId = 0;
            Interlocked.Exchange(ref _lifecycle, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _lifecycle) == 2)
        {
            return;
        }

        if (_ownerThreadId != 0 && _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "The tray icon must be disposed on the thread that started it.");
        }

        if (Interlocked.Exchange(ref _lifecycle, 2) == 2)
        {
            return;
        }

        DisposeResources();
        _ownerThreadId = 0;
    }

    internal void InvokeShowWorkspace()
    {
        var (target, timestamp) = CaptureInvocation();
        PublishCommand(TrayIconCommand.ShowWorkspace, target, timestamp);
    }

    internal void CaptureContextInvocation()
    {
        (_contextForegroundTarget, _contextRequestedTimestamp) = CaptureInvocation();
    }

    internal void InvokeContextCommand(TrayIconCommand command)
    {
        var target = _contextForegroundTarget;
        var timestamp = _contextRequestedTimestamp;
        if (timestamp == 0)
        {
            (target, timestamp) = CaptureInvocation();
        }

        PublishCommand(command, target, timestamp);
    }

    internal void ResetContextInvocation()
    {
        _contextForegroundTarget = ForegroundTarget.None;
        _contextRequestedTimestamp = 0;
    }

    internal void InvokeContextMenu(bool keyboardInvocation)
    {
        if (_contextMenuOpen)
        {
            return;
        }

        var taskbarIcon = _taskbarIcon;
        if (taskbarIcon is null)
        {
            PublishFault(new InvalidOperationException(
                "The tray menu was requested before the tray icon was created."));
            return;
        }

        _contextMenuOpen = true;
        try
        {
            if (_contextRequestedTimestamp == 0)
            {
                CaptureContextInvocation();
            }

            var command = _trayMenuService.Show(
                taskbarIcon.TrayIcon.WindowHandle,
                taskbarIcon.Id,
                keyboardInvocation);
            if (command is { } selectedCommand)
            {
                InvokeContextCommand(selectedCommand);
            }
        }
        catch (Exception exception)
        {
            PublishFault(exception);
        }
        finally
        {
            ResetContextInvocation();
            _contextMenuOpen = false;
        }
    }

    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => InvokeShowWorkspace();

    private void OnTrayRightMouseDown(object sender, RoutedEventArgs e) => CaptureContextInvocation();

    private void OnTrayRightMouseUp(object sender, RoutedEventArgs e) =>
        InvokeContextMenu(keyboardInvocation: false);

    private void OnTrayKeyboardContextMenu(object sender, RoutedEventArgs e)
    {
        CaptureContextInvocation();
        InvokeContextMenu(keyboardInvocation: true);
    }

    private (ForegroundTarget Target, long Timestamp) CaptureInvocation()
    {
        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            return (_foregroundWindowService.Capture(), timestamp);
        }
        catch (Exception exception)
        {
            PublishFault(exception);
            return (ForegroundTarget.None, timestamp);
        }
    }

    private void PublishCommand(
        TrayIconCommand command,
        ForegroundTarget foregroundTarget,
        long requestedTimestamp)
    {
        var handlers = CommandInvoked?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new TrayIconCommandEventArgs(
            command,
            foregroundTarget,
            requestedTimestamp);
        foreach (var handler in handlers.Cast<EventHandler<TrayIconCommandEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                PublishFault(exception);
            }
        }
    }

    private void PublishFault(Exception exception)
    {
        Trace.TraceError(exception.ToString());
        var handlers = Faulted?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<Exception>>())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception subscriberException)
            {
                Trace.TraceError(subscriberException.ToString());
            }
        }
    }

    private void DisposeResources()
    {
        List<Exception>? failures = null;
        if (_taskbarIcon is not null)
        {
            var taskbarIcon = _taskbarIcon;
            _taskbarIcon = null;
            try
            {
                taskbarIcon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
                taskbarIcon.TrayRightMouseDown -= OnTrayRightMouseDown;
                taskbarIcon.TrayRightMouseUp -= OnTrayRightMouseUp;
                taskbarIcon.TrayKeyboardContextMenu -= OnTrayKeyboardContextMenu;
                taskbarIcon.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        var icon = _icon;
        _icon = null;
        try
        {
            icon?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        ResetContextInvocation();
        _contextMenuOpen = false;
        if (failures is not null)
        {
            PublishFault(failures.Count == 1
                ? failures[0]
                : new AggregateException("One or more tray resources failed to dispose.", failures));
        }
    }

    private static Icon LoadProcessIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processIcon = Icon.ExtractAssociatedIcon(processPath);
            if (processIcon is not null)
            {
                return processIcon;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
