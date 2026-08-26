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

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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

        e.Handled = true;
        HideRequested?.Invoke(
            this,
            new WorkspaceHideRequestedEventArgs(WorkspaceHideReason.Escape));
    }

}
