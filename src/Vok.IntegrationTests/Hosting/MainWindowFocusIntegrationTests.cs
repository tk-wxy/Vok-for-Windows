using System.Windows;
using System.Windows.Controls;
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
                Assert.False(openSettingsButton.IsVisible);
                Assert.True(FocusAtInputPriority(window));
                Assert.True(window.HasInputAnchorFocus);

                viewModel.Settings
                    .BeginHotkeyRecordingAsync()
                    .GetAwaiter()
                    .GetResult();
                WorkspaceHideReason? recordingEscapeReason = null;
                EventHandler<WorkspaceHideRequestedEventArgs> recordingEscapeHandler =
                    (_, eventArgs) => recordingEscapeReason = eventArgs.Reason;
                window.HideRequested += recordingEscapeHandler;
                window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    Environment.TickCount,
                    System.Windows.Input.Key.Escape)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                });
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
