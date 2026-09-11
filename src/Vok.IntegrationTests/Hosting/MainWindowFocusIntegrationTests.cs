using System.Diagnostics;
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
using Vok.Core.Launcher;
using Vok.Core.Stage;
using Vok.Features.Settings;
using Vok.Features.Launcher;
using Vok.Features.Clipboard;
using Vok.Features.Stage;
using Vok.Core.Platform;
using Vok.Core.Clipboard;
using Vok.Hotkeys;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

[Collection(WpfApplicationCollection.Name)]
public sealed class MainWindowFocusIntegrationTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void ExternalFileDragReadsForeignDataOnlyOnceAtDropAndAlwaysClaimsItsTerminal()
    {
        wpf.Invoke(() =>
        {
            var appearance = new AppearanceController(
                new InMemorySettingsStore(),
                new WpfAppThemeApplier(wpf.Application));
            appearance.InitializeAsync().GetAwaiter().GetResult();
            var hotkeys = new HotkeyController(
                new InMemorySettingsStore(),
                new FakeConfigurableHotkey());
            hotkeys.InitializeAsync().GetAwaiter().GetResult();
            var viewModel = new MainWindowViewModel(
                new SettingsViewModel(appearance, hotkeys));
            using var launcherViewModel = CreateLauncherViewModel(itemCount: 1);
            var window = new MainWindow(
                viewModel,
                CreateLauncherLayouts(900),
                launcherViewModel);
            var ended = new List<WorkspaceExternalDragEndedEventArgs>();
            window.ExternalDragEnded += (_, eventArgs) => ended.Add(eventArgs);

            var data = new CountingFileDropDataObject([@"C:\Apps\Alpha.exe"]);
            Assert.True(window.BeginExternalFileDrag(data, canDrop: true));
            var overlay = Assert.IsType<Border>(
                window.LauncherPanel.FindName("DropOverlay"));
            var launcherItems = Assert.IsType<ListBox>(
                window.LauncherPanel.FindName("LauncherItemsList"));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.False(launcherItems.IsHitTestVisible);
            for (var index = 0; index < 100; index++)
            {
                window.UpdateExternalFileDrag(canDrop: index % 2 == 0);
            }

            Assert.Equal(1, data.GetDataPresentCallCount);
            Assert.Equal(0, data.GetDataCallCount);

            var accepted = window.CompleteExternalFileDrop(data, canDrop: true);

            Assert.True(accepted.Accepted);
            Assert.Equal(@"C:\Apps\Alpha.exe", Assert.Single(accepted.Paths));
            Assert.Equal(1, data.GetDataCallCount);
            Assert.True(Assert.Single(ended).DropAccepted);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.True(launcherItems.IsHitTestVisible);

            var withdrawingData = new CountingFileDropDataObject(
                [@"C:\Apps\Withdrawn.exe"],
                throwWhenReading: true);
            Assert.True(window.BeginExternalFileDrag(withdrawingData, canDrop: true));

            var rejected = window.CompleteExternalFileDrop(
                withdrawingData,
                canDrop: true);
            window.UpdateExternalFileDrag(canDrop: true);

            Assert.False(rejected.Accepted);
            Assert.Equal(1, withdrawingData.GetDataCallCount);
            Assert.False(ended[^1].DropAccepted);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.True(launcherItems.IsHitTestVisible);

            var canceledInsideWindow = new CountingFileDropDataObject(
                [@"C:\Apps\Canceled.exe"]);
            Assert.True(window.BeginExternalFileDrag(canceledInsideWindow, canDrop: true));
            var dragLeave = Assert.IsType<DragEventArgs>(Activator.CreateInstance(
                typeof(DragEventArgs),
                System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic,
                binder: null,
                [
                    canceledInsideWindow,
                    DragDropKeyStates.LeftMouseButton,
                    DragDropEffects.Copy,
                    window,
                    new Point(10, 10),
                ],
                culture: null));
            dragLeave.RoutedEvent = DragDrop.PreviewDragLeaveEvent;
            window.RaiseEvent(dragLeave);
            Assert.False(ended[^1].DropAccepted);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.True(launcherItems.IsHitTestVisible);

            window.Show();
            try
            {
                var replacedData = new CountingFileDropDataObject(
                    [@"C:\Apps\Replaced.exe"]);
                Assert.True(window.BeginExternalFileDrag(replacedData, canDrop: true));
                EventHandler<WorkspaceExternalDragEndedEventArgs> hideOnEnd =
                    (_, _) => window.Hide();
                window.ExternalDragEnded += hideOnEnd;
                try
                {
                    Assert.False(window.BeginExternalFileDrag(
                        new CountingFileDropDataObject([@"C:\Apps\Next.exe"]),
                        canDrop: true));
                }
                finally
                {
                    window.ExternalDragEnded -= hideOnEnd;
                }

                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.True(launcherItems.IsHitTestVisible);
            }
            finally
            {
                window.Hide();
            }
        });
    }

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
            var launcherLayouts = CreateLauncherLayouts(620, 900);
            using var launcherViewModel = CreateLauncherViewModel(itemCount: 40);
            var thumbnailCache = new StaticClipboardThumbnailCache(
                CreateClipboardThumbnail());
            using var clipboardViewModel = new ClipboardViewModel(
                new NoopClipboardRepository(),
                filePresentation: null,
                thumbnailCache,
                imageAssets: null);
            using var stageViewModel = new StageViewModel(
                new StaticStageRepository(itemCount: 20),
                new NoopStageFileService());
            stageViewModel.InitializeAsync().GetAwaiter().GetResult();
            for (var index = 0; index < 80; index++)
            {
                clipboardViewModel.ApplyCaptured(new ClipboardEntry(
                    Guid.NewGuid(),
                    ClipboardContentKind.Text,
                    (index + 1).ToString("x64"),
                    $"剪贴板文本 {index}\n第二行用于验证换行与省略。",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    index + 1,
                    1,
                    index == 79,
                    1));
            }
            clipboardViewModel.ApplyCaptured(new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.FileList,
                new string('f', 64),
                "报告.docx + 另外 1 项",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                81,
                1,
                false,
                1,
                ClipboardFileTransferIntent.Move,
                2));
            clipboardViewModel.ApplyCaptured(new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Image,
                new string('e', 64),
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                82,
                1,
                false,
                1,
                Image: new ClipboardImageMetadata(
                    1920,
                    1080,
                    true,
                    96000,
                    96000,
                    new ClipboardAssetReference(
                        new string('d', 64),
                        ClipboardAssetState.Ready,
                        1),
                    new ClipboardAssetReference(
                        new string('c', 64),
                        ClipboardAssetState.Ready,
                        1))));
            var window = new MainWindow(
                viewModel,
                launcherLayouts,
                launcherViewModel,
                clipboardViewModel,
                stageViewModel)
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
                CaptureWindowIfRequested(window, "workbench-layout-620-light.png");
                AssertFlexibleWorkspaceHeight(window, workspace, 900);

                var search = Assert.IsType<Border>(window.FindName("HeaderSearchPlaceholder"));
                Assert.False(search.IsHitTestVisible);
                Assert.Equal(480, search.MaxWidth);

                var launcher = Assert.IsType<LauncherView>(window.FindName("LauncherPanel"));
                var launcherLayout = Assert.IsType<Grid>(launcher.Content);
                AssertPanelUsesCompactFixedHeaderAndFlexibleBody(launcherLayout);
                var launcherList = Assert.IsType<ListBox>(
                    launcher.FindName("LauncherItemsList"));
                Assert.Equal(10, launcherViewModel.Rows.Count);
                Assert.Equal(40, launcherViewModel.Count);
                AssertLauncherCardsFillFourColumnLayout(
                    launcher,
                    launcherList,
                    launcherViewModel);
                var stageView = Assert.IsType<StageView>(window.FindName("StagePanel"));
                var stageItemsList = Assert.IsType<ListBox>(
                    stageView.FindName("StageItemsList"));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                var stageCard = FindVisualDescendants<Border>(stageItemsList)
                    .First(border => string.Equals(
                        border.Name,
                        "StageItemCard",
                        StringComparison.Ordinal));
                var launcherTile = FindVisualDescendants<Button>(launcherList)
                    .First(button => button.DataContext is LauncherItemViewModel);
                Assert.Equal(launcher.CardSurfaceHeight, stageView.CardHeight, precision: 8);
                Assert.Equal(launcher.CardUnitHeight, stageView.CardRowUnitHeight, precision: 8);
                Assert.Equal(launcher.CardSurfaceHeight, stageCard.ActualHeight, precision: 8);
                Assert.Equal(
                    launcherTile.TransformToAncestor(window).Transform(new Point()).Y,
                    stageCard.TransformToAncestor(window).Transform(new Point()).Y,
                    precision: 3);
                Assert.Equal(8, stageView.CardLayoutProfile?.ColumnCount);
                window.Width = 1600;
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(10, stageView.CardLayoutProfile?.ColumnCount);
                Assert.Equal(launcher.CardSurfaceHeight, stageView.CardHeight, precision: 8);
                Assert.Equal(launcher.CardUnitHeight, stageView.CardRowUnitHeight, precision: 8);
                CaptureWindowIfRequested(window, "workbench-layout-1600-light.png");
                window.Width = 1440;
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(2, launcherLayouts.CalculationCount);
                var dropOverlay = Assert.IsType<Border>(launcher.FindName("DropOverlay"));
                Assert.Equal(2, Grid.GetRowSpan(dropOverlay));
                Assert.Equal(20, Panel.GetZIndex(dropOverlay));
                Assert.Equal(default, dropOverlay.Margin);
                Assert.Equal(default, dropOverlay.CornerRadius);
                Assert.Equal(new Thickness(2), dropOverlay.BorderThickness);
                Assert.Null(dropOverlay.Background);
                Assert.Null(dropOverlay.Child);
                launcher.SetExternalDragActive(isActive: true, canDrop: true);
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, dropOverlay.Visibility);
                Assert.Equal(launcher.ActualWidth, dropOverlay.ActualWidth, precision: 3);
                Assert.Equal(launcher.ActualHeight, dropOverlay.ActualHeight, precision: 3);
                Assert.False(launcherList.IsHitTestVisible);
                CaptureWindowIfRequested(window, "workbench-launcher-drop-light.png");
                launcher.SetExternalDragActive(isActive: true, canDrop: false);
                Assert.Equal(Visibility.Collapsed, dropOverlay.Visibility);
                Assert.False(launcherList.IsHitTestVisible);
                launcher.SetExternalDragActive(isActive: false, canDrop: false);
                Assert.True(launcherList.IsHitTestVisible);

                var launchTile = FindVisualDescendants<Button>(launcherList)
                    .First(button => button.DataContext is LauncherItemViewModel);
                var launchItem = Assert.IsType<LauncherItemViewModel>(launchTile.DataContext);
                var launchCount = 0;
                launcherViewModel.OpenActionAsync = _ =>
                {
                    launchCount++;
                    return Task.CompletedTask;
                };
                launcher.BeginLaunchFeedback(launchTile, launchItem);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(0, launchCount);
                }
                Assert.True(PumpDispatcherUntil(
                    window.Dispatcher,
                    () => launchCount == 1,
                    TimeSpan.FromSeconds(1)));
                Assert.Equal(1, launchCount);

                if (SystemParameters.ClientAreaAnimation)
                {
                    launcher.BeginLaunchFeedback(launchTile, launchItem);
                    launcher.CancelAllInteractions();
                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(550));
                    Assert.Equal(1, launchCount);
                }

                var stage = Assert.IsType<Border>(window.FindName("StageRegion"));
                var clipboard = Assert.IsType<Border>(window.FindName("ClipboardRegion"));
                var stagePanel = Assert.IsType<StageView>(stage.Child);
                var stageLayout = Assert.IsType<Grid>(stagePanel.Content);
                Assert.Collection(
                    stageLayout.RowDefinitions,
                    row => Assert.Equal(new GridLength(24), row.Height),
                    row => Assert.Equal(new GridLength(1, GridUnitType.Star), row.Height),
                    row => Assert.Equal(new GridLength(28), row.Height));
                var stageList = Assert.IsType<ListBox>(stagePanel.FindName("StageItemsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(stageList));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(stageList));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(stageList));
                Assert.Equal(
                    ScrollBarVisibility.Hidden,
                    ScrollViewer.GetVerticalScrollBarVisibility(stageList));
                var clipboardPanel = Assert.IsType<ClipboardView>(clipboard.Child);
                var clipboardLayout = Assert.IsType<Grid>(clipboardPanel.Content);
                Assert.Collection(
                    clipboardLayout.RowDefinitions,
                    row => Assert.Equal(new GridLength(24), row.Height),
                    row => Assert.Equal(new GridLength(1, GridUnitType.Star), row.Height),
                    row => Assert.Equal(new GridLength(28), row.Height));
                var clipboardList = Assert.IsType<ListBox>(
                    clipboardPanel.FindName("ClipboardItemsList"));
                var clipboardScrollViewer = Assert.IsType<ScrollViewer>(
                    FindVisualDescendant<ScrollViewer>(clipboardList));
                Assert.Equal(
                    ScrollBarVisibility.Hidden,
                    clipboardScrollViewer.VerticalScrollBarVisibility);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(clipboardList));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(clipboardList));
                Assert.Equal(82, clipboardList.Items.Count);
                Assert.True(
                    FindVisualDescendants<ListBoxItem>(clipboardList).Count() < 82,
                    "The clipboard list realized every card instead of virtualizing its viewport.");
                Assert.True(PumpDispatcherUntil(
                    window.Dispatcher,
                    () => FindVisualDescendants<Image>(clipboardList).Any(image =>
                        string.Equals(image.Name, "ThumbnailImage", StringComparison.Ordinal) &&
                        image.Source is not null),
                    TimeSpan.FromSeconds(1)));
                var thumbnailImage = FindVisualDescendants<Image>(clipboardList)
                    .First(image =>
                        string.Equals(image.Name, "ThumbnailImage", StringComparison.Ordinal) &&
                        image.Source is not null);
                Assert.Equal(Stretch.Uniform, thumbnailImage.Stretch);
                Assert.Equal(StretchDirection.DownOnly, thumbnailImage.StretchDirection);
                var transparencyBackground = Assert.IsType<Border>(thumbnailImage.Parent);
                var checkerboard = Assert.IsType<DrawingBrush>(transparencyBackground.Background);
                Assert.Equal(TileMode.Tile, checkerboard.TileMode);
                var imagePasteButton = FindVisualDescendants<Button>(clipboardList)
                    .First(button =>
                        button.DataContext is ClipboardItemViewModel { IsImage: true } &&
                        Equals(button.Command, clipboardViewModel.PasteCommand));
                Assert.Contains(
                    "图片剪贴板，1920×1080",
                    System.Windows.Automation.AutomationProperties.GetName(imagePasteButton));
                var filePasteButton = FindVisualDescendants<Button>(clipboardList)
                    .First(button =>
                        button.DataContext is ClipboardItemViewModel { IsFileList: true } &&
                        Equals(button.Command, clipboardViewModel.PasteCommand));
                Assert.Contains("移动源文件", filePasteButton.ToolTip?.ToString());
                Assert.Contains(
                    "粘贴可能移动源文件",
                    System.Windows.Automation.AutomationProperties.GetName(filePasteButton));
                var fileCopyButton = FindVisualDescendants<Button>(clipboardList)
                    .First(button =>
                        button.DataContext is ClipboardItemViewModel { IsFileList: true } &&
                        Equals(button.Command, clipboardViewModel.CopyFileCommand));
                Assert.Equal(Visibility.Visible, fileCopyButton.Visibility);
                Assert.Equal(
                    "复制文件列表，不移动源文件",
                    System.Windows.Automation.AutomationProperties.GetName(fileCopyButton));

                var bottomBar = Assert.IsType<Border>(window.FindName("BottomBar"));
                Assert.True(bottomBar.IsVisible);

                CaptureWindowIfRequested(window, "workbench-layout-light.png");
                CaptureLauncherHoverIfRequested(
                    window,
                    launcherList,
                    "workbench-launcher-hover-light.png");
                viewModel.NavigateTo(WorkspaceSection.Settings);
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
                CaptureWindowIfRequested(window, "workbench-settings-light.png");
                viewModel.CloseSettingsCommand.Execute(null);
                appearance.ChangeThemeAsync(AppThemeMode.Dark).GetAwaiter().GetResult();
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
                CaptureWindowIfRequested(window, "workbench-layout-dark.png");
                launcher.SetExternalDragActive(isActive: true, canDrop: true);
                window.UpdateLayout();
                CaptureWindowIfRequested(window, "workbench-launcher-drop-dark.png");
                launcher.SetExternalDragActive(isActive: false, canDrop: false);
                CaptureLauncherHoverIfRequested(
                    window,
                    launcherList,
                    "workbench-launcher-hover-dark.png");
                viewModel.NavigateTo(WorkspaceSection.Settings);
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
                CaptureWindowIfRequested(window, "workbench-settings-dark.png");
                viewModel.CloseSettingsCommand.Execute(null);

                AssertLauncherScrollPreservesArbitraryOffset(launcher, launcherList);
                AssertScrollPreservesArbitraryOffset(clipboardList);
                for (var iteration = 0; iteration < 10; iteration++)
                {
                    window.Hide();
                    window.Show();
                    window.UpdateLayout();
                }
                Assert.Equal(2, launcherLayouts.CalculationCount);
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
            var window = new MainWindow(
                viewModel,
                CreateLauncherLayouts(SystemParameters.WorkArea.Height));
            try
            {
                window.Show();
                window.Width = 520;
                window.Height = 500;
                window.UpdateLayout();
                var openSettingsButton = Assert.IsType<Button>(
                    window.FindName("OpenSettingsButton"));
                var openSettingsStyle = Assert.IsType<Style>(
                    openSettingsButton.ReadLocalValue(FrameworkElement.StyleProperty));
                Assert.Same(
                    openSettingsButton.TryFindResource(typeof(Button)),
                    openSettingsStyle.BasedOn);
                Assert.Equal(40, openSettingsButton.ActualWidth);
                Assert.Equal(40, openSettingsButton.ActualHeight);
                AssertFullyInside(openSettingsButton, window);
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
                settingsOverlay.UpdateLayout();
                var settingsWindow = Assert.IsType<Border>(
                    settingsOverlay.FindName("SettingsWindow"));
                Assert.True(double.IsNaN(settingsWindow.Width));
                Assert.True(double.IsNaN(settingsWindow.Height));
                Assert.Equal(760, settingsWindow.MaxWidth);
                Assert.Equal(560, settingsWindow.MaxHeight);
                Assert.True(settingsWindow.ActualWidth <= window.ActualWidth - 48 + 0.001);
                Assert.True(settingsWindow.ActualHeight <= window.ActualHeight - 48 + 0.001);
                AssertFullyInside(settingsWindow, settingsOverlay);
                var settingsCloseButton = Assert.IsType<Button>(
                    settingsOverlay.FindName("SettingsCloseButton"));
                var settingsCloseStyle = Assert.IsType<Style>(
                    settingsCloseButton.ReadLocalValue(FrameworkElement.StyleProperty));
                Assert.NotNull(settingsCloseStyle.BasedOn);
                Assert.Equal(typeof(Button), settingsCloseStyle.BasedOn.TargetType);
                AssertFullyInside(settingsCloseButton, settingsWindow);
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

    [Fact]
    public void EscapeCancelsDirectStagePreviewBeforeHidingWorkspace()
    {
        wpf.Invoke(() =>
        {
            var settingsStore = new InMemorySettingsStore();
            var appearance = new AppearanceController(
                settingsStore,
                new WpfAppThemeApplier(wpf.Application));
            appearance.InitializeAsync().GetAwaiter().GetResult();
            var hotkeys = new HotkeyController(
                settingsStore,
                new FakeConfigurableHotkey());
            hotkeys.InitializeAsync().GetAwaiter().GetResult();
            var mainViewModel = new MainWindowViewModel(
                new SettingsViewModel(appearance, hotkeys));
            using var stageViewModel = new StageViewModel(
                new StaticStageRepository(itemCount: 4),
                new NoopStageFileService());
            stageViewModel.InitializeAsync().GetAwaiter().GetResult();
            var originalIds = stageViewModel.Items.Select(static item => item.Id).ToArray();
            var window = new MainWindow(
                mainViewModel,
                CreateLauncherLayouts(500),
                stageViewModel: stageViewModel)
            {
                Width = 900,
                Height = 500,
            };
            WorkspaceHideReason? hideReason = null;
            window.HideRequested += (_, eventArgs) => hideReason = eventArgs.Reason;

            window.Show();
            try
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                var generation = stageViewModel.BeginReorder(stageViewModel.Items[2]);
                Assert.True(stageViewModel.PreviewReorder(generation, 0));
                Assert.Equal(
                    originalIds,
                    stageViewModel.Items.Select(static item => item.Id));

                RaiseEscape(window);
                Assert.Null(hideReason);
                Assert.False(stageViewModel.IsReordering);
                Assert.Equal(
                    originalIds,
                    stageViewModel.Items.Select(static item => item.Id));

                RaiseEscape(window);
                Assert.Equal(WorkspaceHideReason.Escape, hideReason);
                hideReason = null;

                generation = stageViewModel.BeginReorder(stageViewModel.Items[1]);
                Assert.True(stageViewModel.PreviewReorder(generation, 3));
                mainViewModel.NavigateTo(WorkspaceSection.Settings);
                Assert.False(stageViewModel.IsReordering);
                Assert.Equal(
                    originalIds,
                    stageViewModel.Items.Select(static item => item.Id));
                mainViewModel.NavigateTo(WorkspaceSection.Workspace);

                RaiseEscape(window);
                Assert.Equal(WorkspaceHideReason.Escape, hideReason);
            }
            finally
            {
                window.Hide();
                window.Close();
            }
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

    private static void AssertLauncherCardsFillFourColumnLayout(
        LauncherView launcher,
        ListBox launcherList,
        LauncherViewModel launcherViewModel)
    {
        var viewport = Assert.IsType<Grid>(launcher.FindName("LauncherItemsViewport"));
        var scrollViewer = Assert.IsType<ScrollViewer>(
            FindVisualDescendant<ScrollViewer>(launcherList));
        Assert.True(scrollViewer.CanContentScroll);
        Assert.Equal(ScrollBarVisibility.Hidden, scrollViewer.VerticalScrollBarVisibility);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(launcherList));
        Assert.Equal(
            VirtualizationMode.Recycling,
            VirtualizingPanel.GetVirtualizationMode(launcherList));
        Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(launcherList));
        Assert.Equal(
            viewport.ActualHeight,
            launcher.VisibleRowCount * launcher.CardUnitHeight,
            precision: 8);

        var tiles = FindVisualDescendants<Button>(launcherList)
            .Where(button => button.DataContext is LauncherItemViewModel)
            .ToArray();
        Assert.NotEmpty(tiles);
        Assert.All(
            tiles,
            tile =>
            {
                Assert.Equal(launcher.CardSurfaceHeight, tile.ActualHeight, precision: 8);
                Assert.Equal(5, tile.Margin.Left);
                Assert.Equal(5, tile.Margin.Right);
                Assert.Equal(3, tile.Margin.Top);
                Assert.Equal(3, tile.Margin.Bottom);
                Assert.True(tile.Focusable);
                Assert.NotNull(tile.ToolTip);
                Assert.NotNull(tile.ContextMenu);
                Assert.Equal(4, tile.ContextMenu.Items.Count);
            });

        var first = tiles[0];
        Assert.Equal(
            launcherList.ActualWidth / 4,
            first.ActualWidth + first.Margin.Left + first.Margin.Right,
            precision: 8);
        _ = first.ApplyTemplate();
        var surface = Assert.IsType<Border>(first.Template.FindName("CardSurface", first));
        var hover = Assert.IsType<Border>(first.Template.FindName("HoverOverlay", first));
        Assert.Equal(10, surface.CornerRadius.TopLeft);
        Assert.Equal(Brushes.Transparent, surface.Background);
        Assert.Equal(0, hover.Opacity);
        Assert.NotNull(hover.Background);
        Assert.NotNull(hover.BorderBrush);

        var hoverTrigger = Assert.Single(
            first.Template.Triggers.OfType<Trigger>(),
            trigger => trigger.Property == UIElement.IsMouseOverProperty);
        Assert.NotEmpty(hoverTrigger.EnterActions);
        Assert.NotEmpty(hoverTrigger.ExitActions);

        var content = Assert.IsType<Grid>(first.Content);
        var iconHost = Assert.IsType<Grid>(content.Children[0]);
        var fallback = Assert.IsType<Border>(iconHost.Children[0]);
        var name = Assert.IsType<TextBlock>(content.Children[1]);
        Assert.Equal(46, fallback.Width);
        Assert.Equal(46, fallback.Height);
        Assert.Equal(launcherViewModel.Items[0].DisplayName, name.Text);
        Assert.Equal(27, name.MaxHeight);
        Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);

        var contextMenu = Assert.IsType<ContextMenu>(first.ContextMenu);
        contextMenu.PlacementTarget = first;
        contextMenu.IsOpen = true;
        try
        {
            first.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
            var open = Assert.IsType<MenuItem>(contextMenu.Items[0]);
            var reveal = Assert.IsType<MenuItem>(contextMenu.Items[1]);
            var remove = Assert.IsType<MenuItem>(contextMenu.Items[3]);
            Assert.Same(launcherViewModel.OpenCommand, open.Command);
            Assert.Same(launcherViewModel.RevealCommand, reveal.Command);
            Assert.Same(launcherViewModel.RemoveCommand, remove.Command);
            Assert.Same(first.DataContext, open.CommandParameter);
        }
        finally
        {
            contextMenu.IsOpen = false;
        }
    }

    private static void AssertLauncherScrollPreservesArbitraryOffset(
        LauncherView launcher,
        ListBox launcherList)
    {
        var scrollViewer = Assert.IsType<ScrollViewer>(
            FindVisualDescendant<ScrollViewer>(launcherList));

        launcher.UpdateLayout();
        Assert.True(scrollViewer.ScrollableHeight > 37.5);
        scrollViewer.ScrollToVerticalOffset(37.5);
        launcher.UpdateLayout();
        Assert.Equal(37.5, scrollViewer.VerticalOffset, precision: 3);
    }

    private static void AssertScrollPreservesArbitraryOffset(ListBox listBox)
    {
        var scrollViewer = Assert.IsType<ScrollViewer>(
            FindVisualDescendant<ScrollViewer>(listBox));
        Assert.Equal(ScrollBarVisibility.Hidden, scrollViewer.VerticalScrollBarVisibility);
        Assert.True(scrollViewer.ScrollableHeight > 37.5);
        scrollViewer.ScrollToVerticalOffset(37.5);
        scrollViewer.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
        scrollViewer.UpdateLayout();
        Assert.Equal(37.5, scrollViewer.VerticalOffset, precision: 3);
    }

    private static void AssertFullyInside(FrameworkElement element, Visual ancestor)
    {
        var bounds = element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var ancestorElement = Assert.IsAssignableFrom<FrameworkElement>(ancestor);
        Assert.True(bounds.Left >= -0.001);
        Assert.True(bounds.Top >= -0.001);
        Assert.True(bounds.Right <= ancestorElement.ActualWidth + 0.001);
        Assert.True(bounds.Bottom <= ancestorElement.ActualHeight + 0.001);
    }

    private static LauncherLayoutProfileCache CreateLauncherLayouts(
        params double[] workAreaHeights)
    {
        var cache = new LauncherLayoutProfileCache();
        cache.Prewarm(workAreaHeights.Select(height => new DisplayWorkArea(
            1920,
            Math.Max(1, (int)Math.Round(height)),
            96,
            96)));
        return cache;
    }

    private static LauncherViewModel CreateLauncherViewModel(int itemCount)
    {
        var viewModel = new LauncherViewModel(
            new StaticLauncherRepository(itemCount),
            new NoIconLauncherShellService());
        viewModel.InitializeAsync().GetAwaiter().GetResult();
        return viewModel;
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

    private static bool PumpDispatcherUntil(
        Dispatcher dispatcher,
        Func<bool> predicate,
        TimeSpan timeout)
    {
        if (predicate())
        {
            return true;
        }

        var deadline = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (predicate() || deadline.Elapsed >= timeout)
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return predicate();
    }

    private static void PumpDispatcherFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static BitmapSource CreateClipboardThumbnail()
    {
        const int width = 16;
        const int height = 9;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var pixel = index / 4;
            pixels[index] = (byte)(40 + pixel % width * 6);
            pixels[index + 1] = (byte)(80 + pixel / width * 12);
            pixels[index + 2] = 210;
            pixels[index + 3] = pixel % 3 == 0 ? (byte)110 : (byte)255;
        }

        var image = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        image.Freeze();
        return image;
    }

    private static void CaptureLauncherHoverIfRequested(
        Window window,
        ListBox launcherList,
        string fileName)
    {
        var tile = FindVisualDescendants<Button>(launcherList)
            .FirstOrDefault(button => button.DataContext is LauncherItemViewModel);
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VOK_CAPTURE_DIR")) ||
            tile is null)
        {
            return;
        }

        _ = tile.ApplyTemplate();
        var hover = Assert.IsType<Border>(tile.Template.FindName("HoverOverlay", tile));
        var scale = Assert.IsType<ScaleTransform>(tile.Template.FindName("CardScale", tile));
        hover.Opacity = 1;
        scale.ScaleX = 1.025;
        scale.ScaleY = 1.025;
        try
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
            CaptureWindowIfRequested(window, fileName);
        }
        finally
        {
            hover.ClearValue(UIElement.OpacityProperty);
            scale.ClearValue(ScaleTransform.ScaleXProperty);
            scale.ClearValue(ScaleTransform.ScaleYProperty);
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject => FindVisualDescendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class StaticLauncherRepository : ILauncherRepository
    {
        private readonly IReadOnlyList<LauncherItem> _items;

        public StaticLauncherRepository(int itemCount)
        {
            var now = DateTimeOffset.UtcNow;
            _items = Enumerable.Range(0, itemCount)
                .Select(index => new LauncherItem(
                    Guid.NewGuid(),
                    LauncherItemKind.File,
                    $"C:\\Apps\\App-{index + 1}.exe",
                    $"0:C:\\Apps\\App-{index + 1}.exe",
                    $"应用 {index + 1}",
                    null,
                    index,
                    1,
                    now,
                    now))
                .ToArray();
        }

        public Task<LauncherPage> LoadPageAsync(
            long? afterSortOrder,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var page = _items
                .Where(item => afterSortOrder is null || item.SortOrder > afterSortOrder)
                .Take(pageSize)
                .ToArray();
            long? next = page.Length == pageSize ? page[^1].SortOrder : null;
            return Task.FromResult(new LauncherPage(page, next, _items.Count));
        }

        public Task<LauncherAddResult> AddAsync(
            IReadOnlyList<LauncherTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherAddResult([], []));

        public Task<bool> RemoveAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> MoveToSlotAsync(
            Guid itemId,
            int slot,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class StaticStageRepository : IStageRepository
    {
        private readonly IReadOnlyList<StageItem> _items;

        public StaticStageRepository(int itemCount)
        {
            var now = DateTimeOffset.UtcNow;
            _items = Enumerable.Range(0, itemCount)
                .Select(index =>
                {
                    var kind = (index % 3) switch
                    {
                        1 => StageItemKind.Directory,
                        2 => StageItemKind.ShellLink,
                        _ => StageItemKind.File,
                    };
                    var suffix = kind switch
                    {
                        StageItemKind.Directory => "",
                        StageItemKind.ShellLink => ".lnk",
                        _ => ".docx",
                    };
                    var target = $@"C:\Stage\中转项目 {index + 1}{suffix}";
                    return new StageItem(
                        Guid.NewGuid(),
                        kind,
                        target,
                        target.ToUpperInvariant(),
                        $"中转项目 {index + 1}{suffix}",
                        itemCount - index,
                        index is 1 or 9,
                        1,
                        now,
                        now);
                })
                .ToArray();
        }

        public Task<StagePage> LoadPageAsync(
            StagePageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _items.Take(pageSize).ToArray();
            return Task.FromResult(new StagePage(
                page,
                page.Length == 0
                    ? null
                    : new StagePageCursor(page[^1].SortOrder, page[^1].Id),
                _items.Count));
        }

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRemoveResult> RemoveAsync(
            Guid itemId,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageItemMutationResult> SetPinnedAsync(
            Guid itemId,
            long expectedRevision,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageRelinkResult> RelinkAsync(
            Guid itemId,
            long expectedRevision,
            StageTargetDescriptor target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StageReorderResult> ReorderAsync(
            StageReorderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopStageFileService : IStageFileService
    {
        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StageTargetResolution>>([]);

        public Task<StageFileAvailability> InspectAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StageFileAvailability.Available);

        public Task OpenAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevealInExplorerAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoIconLauncherShellService : ILauncherShellService
    {
        public Task<IReadOnlyList<LauncherTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LauncherTargetResolution>>([]);

        public Task<LauncherIconData?> GetIconAsync(
            LauncherItem item,
            int pixelSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LauncherIconData?>(null);

        public Task OpenAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevealInExplorerAsync(
            LauncherItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class CountingFileDropDataObject(
        string[] paths,
        bool throwWhenReading = false) : IDataObject
    {
        public int GetDataPresentCallCount { get; private set; }

        public int GetDataCallCount { get; private set; }

        public object GetData(string format, bool autoConvert)
        {
            GetDataCallCount++;
            if (throwWhenReading)
            {
                throw new System.Runtime.InteropServices.COMException("The source withdrew FileDrop.");
            }

            return format == DataFormats.FileDrop ? paths : null!;
        }

        public object GetData(string format) => GetData(format, autoConvert: true);

        public object GetData(Type format) => GetData(format.FullName ?? format.Name);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            GetDataPresentCallCount++;
            return format == DataFormats.FileDrop;
        }

        public bool GetDataPresent(string format) =>
            GetDataPresent(format, autoConvert: true);

        public bool GetDataPresent(Type format) =>
            GetDataPresent(format.FullName ?? format.Name);

        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop];

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();

        public void SetData(string format, object data) =>
            throw new NotSupportedException();

        public void SetData(Type format, object data) =>
            throw new NotSupportedException();

        public void SetData(object data) => throw new NotSupportedException();
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

    private sealed class StaticClipboardThumbnailCache(BitmapSource image)
        : IClipboardThumbnailCache
    {
        public Task<ClipboardThumbnailPresentation> LoadAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ClipboardThumbnailPresentation(
                assetHash,
                ClipboardAssetState.Ready,
                expectedRevision,
                image));
        }
    }

    private sealed class NoopClipboardRepository : IClipboardHistoryRepository
    {
        public Task<ClipboardPage> LoadPageAsync(
            ClipboardPageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardPage([], null, 0));

        public Task<ClipboardEntry> UpsertTextAsync(
            ClipboardTextContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardEntry> UpsertFileListAsync(
            ClipboardFileListContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardReplayPayload?>(null);

        public Task<ClipboardEntry?> SetPinnedAsync(
            Guid entryId,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ClipboardEntry?>(null);

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

}
