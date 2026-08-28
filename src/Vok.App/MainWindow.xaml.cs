using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Vok.Core.Workspace;
using Vok.Features.Launcher;
using Vok.Hosting;
using Vok.Presentation;

namespace Vok;

public partial class MainWindow : Window, IWorkspaceWindow
{
    private Application? _application;
    private readonly MainWindowViewModel _viewModel;
    private readonly LauncherViewModel? _launcherViewModel;
    private long _nextExternalDragId;
    private long _activeExternalDragId;

    public MainWindow(
        MainWindowViewModel viewModel,
        LauncherLayoutProfileCache launcherLayoutProfiles,
        LauncherViewModel? launcherViewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _launcherViewModel = launcherViewModel;
        DataContext = viewModel;
        if (launcherViewModel is not null)
        {
            LauncherPanel.DataContext = launcherViewModel;
        }
        LauncherPanel.UseLayoutProfiles(launcherLayoutProfiles);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    public event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;

    public event EventHandler? ApplicationDeactivated;

    public event EventHandler? InputAnchorFocused;

    public event EventHandler<WorkspaceExternalDragStartedEventArgs>? ExternalDragStarted;

    public event EventHandler<WorkspaceExternalDragEndedEventArgs>? ExternalDragEnded;

    public bool FocusInputAnchor()
    {
        return Keyboard.Focus(WorkspaceInputAnchor) == WorkspaceInputAnchor;
    }

    public bool HasInputAnchorFocus =>
        Keyboard.FocusedElement == WorkspaceInputAnchor && WorkspaceInputAnchor.IsKeyboardFocused;

    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    public void PostInput(Action action) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, action);

    public void PostNormal(Action action) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    public void PostBackground(Action action) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    public IDisposable ScheduleNormal(TimeSpan dueTime, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);

        var registration = new DispatcherTimerRegistration(
            new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = dueTime,
            },
            action);
        registration.Start();
        return registration;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        WorkspaceInputAnchor.GotKeyboardFocus += OnWorkspaceInputAnchorGotKeyboardFocus;
        _application = Application.Current;
        if (_application is not null)
        {
            _application.Deactivated += OnApplicationDeactivated;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        EndExternalDrag(dropAccepted: false);
        IsVisibleChanged -= OnWindowIsVisibleChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_application is not null)
        {
            _application.Deactivated -= OnApplicationDeactivated;
            _application = null;
        }

        base.OnClosed(e);
    }

    private void OnWindowIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            EndExternalDrag(dropAccepted: false);
        }
    }

    private void OnWorkspaceInputAnchorGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(e.NewFocus, WorkspaceInputAnchor))
        {
            InputAnchorFocused?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsWorkspaceVisible) &&
            _viewModel.IsWorkspaceVisible &&
            IsVisible)
        {
            PostInput(() => _ = FocusInputAnchor());
        }
    }

    private void OnApplicationDeactivated(object? sender, EventArgs e) =>
        ApplicationDeactivated?.Invoke(this, EventArgs.Empty);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            return;
        }

        e.Cancel = true;
        HideRequested?.Invoke(
            this,
            new WorkspaceHideRequestedEventArgs(WorkspaceHideReason.UserAction));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.Settings.IsHotkeyRecording)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.IsSettingsVisible)
        {
            _viewModel.NavigateTo(WorkspaceSection.Workspace);
            return;
        }

        HideRequested?.Invoke(
            this,
            new WorkspaceHideRequestedEventArgs(WorkspaceHideReason.Escape));
    }

    private void OnPreviewDragEnter(object sender, DragEventArgs e) =>
        UpdateExternalDrag(e);

    private void OnPreviewDragOver(object sender, DragEventArgs e) =>
        UpdateExternalDrag(e);

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X >= 0 && point.Y >= 0 &&
            point.X <= ActualWidth && point.Y <= ActualHeight)
        {
            return;
        }

        EndExternalDrag(dropAccepted: false);
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        IReadOnlyList<string> paths = [];
        var accepted = _launcherViewModel is not null &&
            TrySnapshotFileDrop(e, out paths) &&
            IsOverLauncher(e);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        // Claim the OLE terminal before dispatching asynchronous catalog work.
        EndExternalDrag(accepted);
        if (accepted && _launcherViewModel is not null)
        {
            _ = _launcherViewModel.AddDroppedPathsAsync(paths);
        }
    }

    private void UpdateExternalDrag(DragEventArgs e)
    {
        if (!TrySnapshotFileDrop(e, out _))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (_activeExternalDragId == 0)
        {
            _activeExternalDragId = Interlocked.Increment(ref _nextExternalDragId);
            ExternalDragStarted?.Invoke(
                this,
                new WorkspaceExternalDragStartedEventArgs(_activeExternalDragId));
        }

        var canDrop = IsOverLauncher(e);
        LauncherPanel.SetExternalDragActive(isActive: true, canDrop);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private bool IsOverLauncher(DragEventArgs e)
    {
        if (_viewModel.IsSettingsVisible || !LauncherPanel.IsVisible)
        {
            return false;
        }

        var point = e.GetPosition(LauncherPanel);
        return point.X >= 0 && point.Y >= 0 &&
            point.X <= LauncherPanel.ActualWidth &&
            point.Y <= LauncherPanel.ActualHeight;
    }

    private static bool TrySnapshotFileDrop(
        DragEventArgs e,
        out IReadOnlyList<string> paths)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true) &&
                e.Data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] values)
            {
                paths = values.ToArray();
                return paths.Count != 0;
            }
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A foreign OLE source can withdraw a format while the drag is terminating.
        }

        paths = [];
        return false;
    }

    private void EndExternalDrag(bool dropAccepted)
    {
        var dragId = _activeExternalDragId;
        if (dragId == 0)
        {
            return;
        }

        _activeExternalDragId = 0;
        LauncherPanel.SetExternalDragActive(isActive: false, canDrop: false);
        ExternalDragEnded?.Invoke(
            this,
            new WorkspaceExternalDragEndedEventArgs(dragId, dropAccepted));
    }

    private sealed class DispatcherTimerRegistration(
        DispatcherTimer timer,
        Action action) : IDisposable
    {
        private Action? _action = action;

        public void Start()
        {
            timer.Tick += OnTick;
            timer.Start();
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= OnTick;
            _action = null;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var callback = _action;
            Dispose();
            callback?.Invoke();
        }
    }

}
