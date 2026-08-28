using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Presentation;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Features.Settings;
using Vok.Features.Launcher;
using Vok.Core.Platform;
using Vok.Hotkeys;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

[Collection(WpfApplicationCollection.Name)]
public sealed class MainWindowFocusIntegrationTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void WorkbenchShellRendersStaticOneTwoOneLayoutInLightAndDarkThemes()
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
            var window = new MainWindow(viewModel)
            {
                Width = 1440,
                Height = 900,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });

                var root = Assert.IsType<Grid>(window.Content);
                Assert.Collection(
                    root.RowDefinitions,
                    row => Assert.Equal(new GridLength(56), row.Height),
                    row => Assert.Equal(new GridLength(1, GridUnitType.Star), row.Height),
                    row => Assert.Equal(new GridLength(36), row.Height));

                var workspace = Assert.IsType<Grid>(window.FindName("MainWorkspaceGrid"));
                Assert.Collection(
                    workspace.ColumnDefinitions,
                    column => Assert.Equal(new GridLength(1, GridUnitType.Star), column.Width),
                    column => Assert.Equal(new GridLength(2, GridUnitType.Star), column.Width),
                    column => Assert.Equal(new GridLength(1, GridUnitType.Star), column.Width));
                AssertFlexibleWorkspaceHeight(window, workspace, 620);
                AssertFlexibleWorkspaceHeight(window, workspace, 900);

                var search = Assert.IsType<Border>(window.FindName("HeaderSearchPlaceholder"));
                Assert.False(search.IsHitTestVisible);
                Assert.Equal(480, search.MaxWidth);

                var launcher = Assert.IsType<LauncherView>(window.FindName("LauncherPanel"));
                var launcherLayout = Assert.IsType<Grid>(launcher.Content);
                AssertPanelUsesCompactFixedHeaderAndFlexibleBody(launcherLayout);
                var launcherGrid = Assert.IsType<UniformGrid>(
                    launcher.FindName("LauncherPlaceholderGrid"));
                Assert.Equal(4, launcherGrid.Columns);
                Assert.Equal(8, launcherGrid.Children.Count);
                Assert.False(launcherGrid.IsHitTestVisible);
                Assert.All(
                    launcherGrid.Children.OfType<Border>(),
                    tile => Assert.Null(tile.ContextMenu));

                var stage = Assert.IsType<Border>(window.FindName("StageRegion"));
                var clipboard = Assert.IsType<Border>(window.FindName("ClipboardRegion"));
                AssertPlaceholderBodyIsEmpty(stage);
                AssertPlaceholderBodyIsEmpty(clipboard);

                var bottomBar = Assert.IsType<Border>(window.FindName("BottomBar"));
                Assert.True(bottomBar.IsVisible);

                CaptureWindowIfRequested(window, "workbench-layout-light.png");
                appearance.ChangeThemeAsync(AppThemeMode.Dark).GetAwaiter().GetResult();
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
                CaptureWindowIfRequested(window, "workbench-layout-dark.png");
            }
            finally
            {
                window.Hide();
            }
        });
    }

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
                Assert.Equal(3, Grid.GetRowSpan(settingsOverlay));
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

    private static void AssertPlaceholderBodyIsEmpty(Border region)
    {
        var layout = Assert.IsType<Grid>(region.Child);
        AssertPanelUsesCompactFixedHeaderAndFlexibleBody(layout);
        Assert.Equal(2, layout.Children.Count);
        var body = Assert.IsType<Grid>(layout.Children[1]);
        Assert.Empty(body.Children);
    }

    private static void AssertPanelUsesCompactFixedHeaderAndFlexibleBody(Grid layout) =>
        Assert.Collection(
            layout.RowDefinitions,
            row => Assert.Equal(new GridLength(24), row.Height),
            row => Assert.Equal(new GridLength(1, GridUnitType.Star), row.Height));

    private static void AssertFlexibleWorkspaceHeight(
        MainWindow window,
        Grid workspace,
        double windowHeight)
    {
        window.Height = windowHeight;
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });

        Assert.Equal(window.ActualHeight - 56 - 36, workspace.ActualHeight, precision: 3);
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

    private static void CaptureWindowIfRequested(Window window, string fileName)
    {
        var captureDirectory = Environment.GetEnvironmentVariable("VOK_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(captureDirectory))
        {
            return;
        }

        Directory.CreateDirectory(captureDirectory);
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(captureDirectory, fileName));
        encoder.Save(stream);
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
        public event EventHandler<HotkeyPressEvent>? PressChanged;
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
            _ = PressChanged;
            _ = Faulted;
        }
    }

}
