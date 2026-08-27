using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Presentation;

namespace Vok;

public partial class MainWindow : Window, IWorkspaceWindow
{
    private Application? _application;
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;

    public event EventHandler? ApplicationDeactivated;

    public event EventHandler? InputAnchorFocused;

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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_application is not null)
        {
            _application.Deactivated -= OnApplicationDeactivated;
            _application = null;
        }

        base.OnClosed(e);
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
