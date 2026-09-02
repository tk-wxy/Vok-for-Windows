using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Win32;
using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Features.Tray;

public sealed class WpfTrayIconService : ITrayIconService
{
    private readonly IForegroundWindowService _foregroundWindowService;
    private readonly ITrayMenuService _trayMenuService;
    private readonly ITrayRegistrationProbe _registrationProbe;
    private TaskbarIcon? _taskbarIcon;
    private TrayRegistrationRecoveryCoordinator? _registrationRecovery;
    private Dispatcher? _ownerDispatcher;
    private Icon? _icon;
    private ForegroundTarget _contextForegroundTarget;
    private long _contextRequestedTimestamp;
    private bool _contextMenuOpen;
    private bool _powerEventsSubscribed;
    private bool _sessionEventsSubscribed;
    private int _ownerThreadId;
    private int _lifecycle;

    public WpfTrayIconService(
        IForegroundWindowService foregroundWindowService,
        ITrayMenuService trayMenuService,
        ITrayRegistrationProbe registrationProbe)
    {
        _foregroundWindowService = foregroundWindowService ??
            throw new ArgumentNullException(nameof(foregroundWindowService));
        _trayMenuService = trayMenuService ??
            throw new ArgumentNullException(nameof(trayMenuService));
        _registrationProbe = registrationProbe ??
            throw new ArgumentNullException(nameof(registrationProbe));
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

        _ownerDispatcher = Dispatcher.CurrentDispatcher;

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
            _registrationRecovery = new TrayRegistrationRecoveryCoordinator(
                _registrationProbe,
                _taskbarIcon.TrayIcon.WindowHandle,
                _taskbarIcon.Id,
                RepairRegistration,
                new DispatcherTrayRecoveryScheduler(_ownerDispatcher),
                TrayRegistrationRecoveryOptions.Default,
                PublishFault);
            _taskbarIcon.TrayIcon.MessageWindow.TaskbarCreated += OnTaskbarCreated;
            SubscribeToEnvironmentEvents();
            _registrationRecovery.Start();
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
        _registrationRecovery?.SetBlocked(true);
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
            _registrationRecovery?.SetBlocked(false);
        }
    }

    internal nint RegistrationOwnerWindow =>
        _taskbarIcon?.TrayIcon.WindowHandle ?? 0;

    internal Guid RegistrationIconId => _taskbarIcon?.Id ?? Guid.Empty;

    internal bool LibraryReportsRegistrationCreated =>
        _taskbarIcon?.IsCreated == true;

    internal TrayRegistrationRecoveryState RegistrationRecoveryState =>
        _registrationRecovery?.State ?? TrayRegistrationRecoveryState.NotStarted;

    internal void RequestRegistrationCheck(TrayRegistrationRecoveryTrigger trigger) =>
        _registrationRecovery?.RequestCheck(trigger);

    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => InvokeShowWorkspace();

    private void OnTrayRightMouseDown(object sender, RoutedEventArgs e) => CaptureContextInvocation();

    private void OnTrayRightMouseUp(object sender, RoutedEventArgs e) =>
        InvokeContextMenu(keyboardInvocation: false);

    private void OnTrayKeyboardContextMenu(object sender, RoutedEventArgs e)
    {
        CaptureContextInvocation();
        InvokeContextMenu(keyboardInvocation: true);
    }

    private void OnTaskbarCreated(object? sender, EventArgs e) =>
        PostRegistrationCheck(TrayRegistrationRecoveryTrigger.TaskbarCreated);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            PostRegistrationCheck(TrayRegistrationRecoveryTrigger.PowerResume);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            PostRegistrationCheck(TrayRegistrationRecoveryTrigger.SessionUnlock);
        }
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

    private void RepairRegistration()
    {
        var taskbarIcon = _taskbarIcon ?? throw new InvalidOperationException(
            "The tray icon is unavailable during registration recovery.");
        if (!taskbarIcon.TrayIcon.TryRemove())
        {
            Trace.TraceWarning(
                "The Shell did not acknowledge removal of tray icon {0:B} before recovery.",
                taskbarIcon.Id);
        }

        taskbarIcon.TrayIcon.Create();
    }

    private void PostRegistrationCheck(TrayRegistrationRecoveryTrigger trigger)
    {
        var dispatcher = _ownerDispatcher;
        var recovery = _registrationRecovery;
        if (dispatcher is null || recovery is null ||
            Volatile.Read(ref _lifecycle) != 1)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () =>
                {
                    if (Volatile.Read(ref _lifecycle) == 1 &&
                        ReferenceEquals(_registrationRecovery, recovery))
                    {
                        recovery.RequestCheck(trigger);
                    }
                });
        }
        catch (InvalidOperationException exception)
        {
            if (Volatile.Read(ref _lifecycle) == 1)
            {
                PublishFault(exception);
            }
        }
    }

    private void SubscribeToEnvironmentEvents()
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerEventsSubscribed = true;
        }
        catch (Exception exception)
        {
            PublishFault(new InvalidOperationException(
                "Unable to observe Windows power resume events for tray recovery.",
                exception));
        }

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _sessionEventsSubscribed = true;
        }
        catch (Exception exception)
        {
            PublishFault(new InvalidOperationException(
                "Unable to observe Windows session unlock events for tray recovery.",
                exception));
        }
    }

    private void UnsubscribeFromEnvironmentEvents()
    {
        if (_powerEventsSubscribed)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _powerEventsSubscribed = false;
        }

        if (_sessionEventsSubscribed)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _sessionEventsSubscribed = false;
        }
    }

    private void DisposeResources()
    {
        List<Exception>? failures = null;
        UnsubscribeFromEnvironmentEvents();
        if (_taskbarIcon is not null)
        {
            _taskbarIcon.TrayIcon.MessageWindow.TaskbarCreated -= OnTaskbarCreated;
        }

        var registrationRecovery = _registrationRecovery;
        _registrationRecovery = null;
        try
        {
            registrationRecovery?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

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
        _ownerDispatcher = null;
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
