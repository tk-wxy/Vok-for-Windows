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
        CancelExternalFileDrag();
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
            LauncherPanel.CancelAllInteractions();
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
        var canDrop = IsOverLauncher(e);
        var acceptedFormat = BeginExternalFileDrag(e.Data, canDrop);
        e.Effects = acceptedFormat && canDrop
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        var canDrop = _activeExternalDragId != 0 && IsOverLauncher(e);
        UpdateExternalFileDrag(canDrop);
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
        var result = CompleteExternalFileDrop(
            e.Data,
            _launcherViewModel is not null && IsOverLauncher(e));
        e.Effects = result.Accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (result.Accepted && _launcherViewModel is not null)
        {
            _ = _launcherViewModel.AddDroppedPathsAsync(result.Paths);
        }
    }

    internal bool BeginExternalFileDrag(IDataObject data, bool canDrop)
    {
        ArgumentNullException.ThrowIfNull(data);
        LauncherPanel.CancelAllInteractions();

        // A fresh DragEnter is a terminal boundary for any source that failed
        // to deliver the preceding Drop/DragLeave callback.
        var replacedActiveDrag = _activeExternalDragId != 0;
        CancelExternalFileDrag();
        if (replacedActiveDrag && !IsVisible)
        {
            // Ending the previous lease can synchronously honor a deferred
            // deactivation and hide the window. Do not create an orphaned UI
            // drag after that reentrant lifecycle transition.
            return false;
        }

        if (!HasFileDrop(data))
        {
            return false;
        }

        _activeExternalDragId = Interlocked.Increment(ref _nextExternalDragId);
        ExternalDragStarted?.Invoke(
            this,
            new WorkspaceExternalDragStartedEventArgs(_activeExternalDragId));
        UpdateExternalFileDrag(canDrop);
        return true;
    }

    internal void UpdateExternalFileDrag(bool canDrop)
    {
        if (_activeExternalDragId == 0)
        {
            return;
        }

        LauncherPanel.SetExternalDragActive(isActive: true, canDrop);
    }

    internal WorkspaceExternalFileDropResult CompleteExternalFileDrop(
        IDataObject data,
        bool canDrop)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dragId = ClaimExternalFileDrag();
        if (dragId == 0)
        {
            return WorkspaceExternalFileDropResult.Rejected;
        }

        IReadOnlyList<string> paths = [];
        var accepted = canDrop && TrySnapshotFileDrop(data, out paths);
        ExternalDragEnded?.Invoke(
            this,
            new WorkspaceExternalDragEndedEventArgs(dragId, accepted));
        return accepted
            ? new WorkspaceExternalFileDropResult(true, paths)
            : WorkspaceExternalFileDropResult.Rejected;
    }

    internal void CancelExternalFileDrag()
    {
        var dragId = ClaimExternalFileDrag();
        if (dragId != 0)
        {
            ExternalDragEnded?.Invoke(
                this,
                new WorkspaceExternalDragEndedEventArgs(dragId, dropAccepted: false));
        }
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

    private static bool HasFileDrop(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySnapshotFileDrop(
        IDataObject data,
        out IReadOnlyList<string> paths)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] values)
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

    private long ClaimExternalFileDrag()
    {
        var dragId = _activeExternalDragId;
        if (dragId == 0)
        {
            return 0;
        }

        // Claim before visual and lifecycle side effects so reentrant or late
        // terminal callbacks cannot complete the same drag twice.
        _activeExternalDragId = 0;
        LauncherPanel.SetExternalDragActive(isActive: false, canDrop: false);
        return dragId;
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

internal sealed record WorkspaceExternalFileDropResult(
    bool Accepted,
    IReadOnlyList<string> Paths)
{
    public static WorkspaceExternalFileDropResult Rejected { get; } = new(false, []);
}
