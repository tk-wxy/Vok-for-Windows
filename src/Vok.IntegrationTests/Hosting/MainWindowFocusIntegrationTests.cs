using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Presentation;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Features.Settings;
using Vok.Core.Platform;
using Vok.Hotkeys;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

[Collection(WpfApplicationCollection.Name)]
public sealed class MainWindowFocusIntegrationTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void InputAnchorFocusClearsAcrossHideAndCanBeReacquiredOnNextShow()
    {
        wpf.Invoke(() =>
        {
            var appearance = new AppearanceController(
                new InMemorySettingsStore(),
                new WpfAppThemeApplier(wpf.Application));
            appearance.InitializeAsync().GetAwaiter().GetResult();
            appearance.ChangeThemeAsync(AppThemeMode.Light).GetAwaiter().GetResult();
            var hotkeys = new HotkeyController(
                new InMemorySettingsStore(),
                new FakeConfigurableHotkey());
            hotkeys.InitializeAsync().GetAwaiter().GetResult();
            var viewModel = new MainWindowViewModel(
                new SettingsViewModel(appearance, hotkeys));
            var window = new MainWindow(viewModel);
            try
            {
                window.Show();
                var openSettingsButton = Assert.IsType<Button>(
                    window.FindName("OpenSettingsButton"));
                Assert.Same(
                    DependencyProperty.UnsetValue,
                    openSettingsButton.ReadLocalValue(FrameworkElement.StyleProperty));
                Assert.Same(
                    openSettingsButton.TryFindResource(typeof(Button)),
                    openSettingsButton.Style);
                var lightBackground = Assert.IsType<SolidColorBrush>(
                    window.FindResource("ApplicationBackgroundBrush"));
                var lightSettingsForeground = Assert.IsType<SolidColorBrush>(
                    openSettingsButton.Foreground).Color;
                var lightSettingsBackground = Assert.IsType<SolidColorBrush>(
                    openSettingsButton.Background).Color;
                Assert.IsType<SolidColorBrush>(
                    window.FindResource("SystemFillColorCriticalBrush"));

                appearance.ChangeThemeAsync(AppThemeMode.Dark).GetAwaiter().GetResult();
                var darkBackground = Assert.IsType<SolidColorBrush>(
                    window.FindResource("ApplicationBackgroundBrush"));
                Assert.NotEqual(lightBackground.Color, darkBackground.Color);
                Assert.NotEqual(
                    lightSettingsForeground,
                    Assert.IsType<SolidColorBrush>(openSettingsButton.Foreground).Color);
                Assert.NotEqual(
                    lightSettingsBackground,
                    Assert.IsType<SolidColorBrush>(openSettingsButton.Background).Color);

                _ = window.Activate();
                Assert.True(FocusAtInputPriority(window));
                Assert.True(window.HasInputAnchorFocus);

                window.Hide();
                Assert.False(window.HasInputAnchorFocus);

                window.Show();
                _ = window.Activate();
                Assert.True(FocusAtInputPriority(window));
                Assert.True(window.HasInputAnchorFocus);

                viewModel.NavigateTo(WorkspaceSection.Settings);
                Assert.True(viewModel.IsSettingsVisible);
                Assert.True(openSettingsButton.IsVisible);
                var settingsOverlay = Assert.IsType<SettingsView>(
                    window.FindName("SettingsOverlay"));
                Assert.True(settingsOverlay.IsVisible);
                Assert.Equal(2, Grid.GetRowSpan(settingsOverlay));
                var settingsWindow = Assert.IsType<Border>(
                    settingsOverlay.FindName("SettingsWindow"));
                Assert.Equal(720, settingsWindow.Width);
                Assert.Equal(540, settingsWindow.Height);
                var settingsCloseButton = Assert.IsType<Button>(
                    settingsOverlay.FindName("SettingsCloseButton"));
                Assert.Same(
                    DependencyProperty.UnsetValue,
                    settingsCloseButton.ReadLocalValue(FrameworkElement.StyleProperty));
                var settingsNavigation = Assert.IsType<ListBox>(
                    settingsOverlay.FindName("SettingsNavigation"));
                var generalPanel = Assert.IsType<ScrollViewer>(
                    settingsOverlay.FindName("GeneralPanel"));
                var hotkeyPanel = Assert.IsType<ScrollViewer>(
                    settingsOverlay.FindName("HotkeyPanel"));
                var aboutPanel = Assert.IsType<ScrollViewer>(
                    settingsOverlay.FindName("AboutPanel"));
                settingsOverlay.UpdateLayout();
                Assert.Equal(Visibility.Visible, generalPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, hotkeyPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, aboutPanel.Visibility);
                settingsNavigation.SelectedIndex = 1;
                settingsOverlay.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, generalPanel.Visibility);
                Assert.Equal(Visibility.Visible, hotkeyPanel.Visibility);
                settingsNavigation.SelectedIndex = 2;
                settingsOverlay.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, hotkeyPanel.Visibility);
                Assert.Equal(Visibility.Visible, aboutPanel.Visibility);
                settingsNavigation.SelectedIndex = 0;
                Assert.True(FocusAtInputPriority(window));
                Assert.True(window.HasInputAnchorFocus);

                WorkspaceHideReason? settingsEscapeReason = null;
                EventHandler<WorkspaceHideRequestedEventArgs> settingsEscapeHandler =
                    (_, eventArgs) => settingsEscapeReason = eventArgs.Reason;
                window.HideRequested += settingsEscapeHandler;
                Assert.Same(
                    settingsCloseButton,
                    Keyboard.Focus(settingsCloseButton));
                RaiseEscape(window);
                window.Dispatcher.Invoke(
                    DispatcherPriority.Input,
                    static () => { });
                window.HideRequested -= settingsEscapeHandler;
                Assert.Null(settingsEscapeReason);
                Assert.True(viewModel.IsWorkspaceVisible);
                Assert.False(settingsOverlay.IsVisible);
                Assert.True(window.HasInputAnchorFocus);

                viewModel.NavigateTo(WorkspaceSection.Settings);
                var settingsMask = Assert.IsType<Grid>(
                    settingsOverlay.FindName("SettingsMask"));
                settingsMask.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                });
                Assert.True(viewModel.IsWorkspaceVisible);
                Assert.False(settingsOverlay.IsVisible);

                viewModel.NavigateTo(WorkspaceSection.Settings);

                viewModel.Settings
                    .BeginHotkeyRecordingAsync()
                    .GetAwaiter()
                    .GetResult();
                WorkspaceHideReason? recordingEscapeReason = null;
                EventHandler<WorkspaceHideRequestedEventArgs> recordingEscapeHandler =
                    (_, eventArgs) => recordingEscapeReason = eventArgs.Reason;
                window.HideRequested += recordingEscapeHandler;
                RaiseEscape(window);
                window.HideRequested -= recordingEscapeHandler;
                Assert.Null(recordingEscapeReason);
                viewModel.Settings
                    .CancelHotkeyRecordingAsync()
                    .GetAwaiter()
                    .GetResult();

                WorkspaceHideReason? closeReason = null;
                window.HideRequested += (_, eventArgs) =>
                {
                    closeReason = eventArgs.Reason;
                    window.Hide();
                };
                window.Close();
                Assert.Equal(WorkspaceHideReason.UserAction, closeReason);
                Assert.False(window.IsVisible);
            }
            finally { window.Hide(); }
        });
    }

    private static bool FocusAtInputPriority(MainWindow window)
    {
        var frame = new DispatcherFrame();
        var focused = false;
        window.PostInput(() =>
        {
            focused = window.FocusInputAnchor();
            frame.Continue = false;
        });
        Dispatcher.PushFrame(frame);
        return focused;
    }

    private static void RaiseEscape(MainWindow window) =>
        window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            System.Windows.Input.Key.Escape)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        });

    private sealed class InMemorySettingsStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);

        public Task SaveThemeModeAsync(
            AppThemeMode themeMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveHotkeyGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeConfigurableHotkey : IConfigurableGlobalHotkeyService
    {
        public event EventHandler<HotkeyInvocation>? Pressed;
        public event EventHandler<Exception>? Faulted;
        public GlobalHotkeyGesture CurrentGesture { get; private set; } =
            GlobalHotkeyGesture.Default;
        public string DisplayGesture => CurrentGesture.DisplayName;
        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SuspendAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ChangeGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default)
        {
            CurrentGesture = gesture;
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            _ = Pressed;
            _ = Faulted;
        }
    }

}
