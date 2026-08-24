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
    private readonly MainWindowViewModel _viewModel;
    private Application? _application;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? ApplicationDeactivated;

    public event EventHandler? InputAnchorFocused;

    public bool FocusInputAnchor()
    {
        return Keyboard.Focus(KeyboardInputProbe) == KeyboardInputProbe;
    }

    public bool HasInputAnchorFocus =>
        Keyboard.FocusedElement == KeyboardInputProbe && KeyboardInputProbe.IsKeyboardFocused;

    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    public void PostInput(Action action) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, action);

    public void PostNormal(Action action) =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        KeyboardInputProbe.GotKeyboardFocus += (_, _) =>
            InputAnchorFocused?.Invoke(this, EventArgs.Empty);
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

    private void OnApplicationDeactivated(object? sender, EventArgs e) =>
        ApplicationDeactivated?.Invoke(this, EventArgs.Empty);

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

    private void OnInputProbeTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        _viewModel.AppendInputProbeText(e.Text);
        e.Handled = true;
    }

    private void OnInputProbeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back)
        {
            return;
        }

        _viewModel.RemoveLastInputProbeTextElement();
        e.Handled = true;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) =>
        HideRequested?.Invoke(
            this,
            new WorkspaceHideRequestedEventArgs(WorkspaceHideReason.UserAction));

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);
}
