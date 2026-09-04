using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Vok.Core.Workspace;
using Vok.Features.Clipboard;
using Vok.Features.Launcher;
using Vok.Features.Stage;
using Vok.Hosting;
using Vok.Presentation;

namespace Vok;

public partial class MainWindow : Window, IWorkspaceWindow
{
    private Application? _application;
    private readonly MainWindowViewModel _viewModel;
    private readonly WorkspaceFileDropRouter _fileDropRouter;

    public MainWindow(
        MainWindowViewModel viewModel,
        LauncherLayoutProfileCache launcherLayoutProfiles,
        LauncherViewModel? launcherViewModel = null,
        ClipboardViewModel? clipboardViewModel = null,
        StageViewModel? stageViewModel = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (launcherViewModel is not null)
        {
            LauncherPanel.DataContext = launcherViewModel;
        }
        if (clipboardViewModel is not null)
        {
            ClipboardPanel.DataContext = clipboardViewModel;
        }
        if (stageViewModel is not null)
        {
            StagePanel.DataContext = stageViewModel;
        }

        _fileDropRouter = new WorkspaceFileDropRouter(
            [
                new LauncherWorkspaceFileDropTarget(LauncherPanel, launcherViewModel),
                new StageWorkspaceFileDropTarget(StagePanel, stageViewModel),
            ],
            dragId => ExternalDragStarted?.Invoke(
                this,
                new WorkspaceExternalDragStartedEventArgs(dragId)),
            (dragId, accepted) => ExternalDragEnded?.Invoke(
                this,
                new WorkspaceExternalDragEndedEventArgs(dragId, accepted)),
            () => IsVisible);
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
        _fileDropRouter.Dispose();
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
            CancelExternalFileDrag();
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
        else if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsVisible) &&
                 _viewModel.IsSettingsVisible)
        {
            CancelExternalFileDrag();
            LauncherPanel.CancelAllInteractions();
        }
    }

    private void OnApplicationDeactivated(object? sender, EventArgs e) =>
        ApplicationDeactivated?.Invoke(this, EventArgs.Empty);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CancelExternalFileDrag();
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

        if (LauncherPanel.TryCancelReorderInteraction())
        {
            e.Handled = true;
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

    private void OnPreviewDragEnter(object sender, DragEventArgs e)
    {
        var acceptedFormat = _fileDropRouter.Begin(e.Data, ResolveDropTarget(e));
        e.Effects = acceptedFormat &&
            _fileDropRouter.CurrentTarget is not WorkspaceFileDropTargetKind.None
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        var canDrop = _fileDropRouter.Update(ResolveDropTarget(e));
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // Window is the single AllowDrop target. Its DragLeave is an OLE
        // terminal even when cancellation leaves the cursor inside the HWND;
        // a coordinate check would turn that valid terminal into a leak.
        CancelExternalFileDrag();
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        var result = _fileDropRouter.Complete(e.Data, ResolveDropTarget(e));
        e.Effects = result.Accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    internal bool BeginExternalFileDrag(IDataObject data, bool canDrop)
    {
        return _fileDropRouter.Begin(
            data,
            canDrop
                ? WorkspaceFileDropTargetKind.Launcher
                : WorkspaceFileDropTargetKind.None);
    }

    internal void UpdateExternalFileDrag(bool canDrop)
    {
        _ = _fileDropRouter.Update(
            canDrop
                ? WorkspaceFileDropTargetKind.Launcher
                : WorkspaceFileDropTargetKind.None);
    }

    internal WorkspaceExternalFileDropResult CompleteExternalFileDrop(
        IDataObject data,
        bool canDrop)
    {
        return _fileDropRouter.Complete(
            data,
            canDrop
                ? WorkspaceFileDropTargetKind.Launcher
                : WorkspaceFileDropTargetKind.None);
    }

    internal void CancelExternalFileDrag()
    {
        _fileDropRouter.Cancel();
    }

    private WorkspaceFileDropTargetKind ResolveDropTarget(DragEventArgs e)
    {
        if (_viewModel.IsSettingsVisible)
        {
            return WorkspaceFileDropTargetKind.None;
        }

        if (LauncherPanel.IsVisible && IsPointInside(e, LauncherPanel))
        {
            return WorkspaceFileDropTargetKind.Launcher;
        }

        return StagePanel.IsVisible && IsPointInside(e, StagePanel)
            ? WorkspaceFileDropTargetKind.Stage
            : WorkspaceFileDropTargetKind.None;
    }

    private static bool IsPointInside(DragEventArgs e, FrameworkElement element)
    {
        var point = e.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 &&
            point.X < element.ActualWidth &&
            point.Y < element.ActualHeight;
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
