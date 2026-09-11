using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageViewIntegrationTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void StageRendersARecyclingFixedPageAndUsesExclusiveDropVisuals()
    {
        wpf.Invoke(() =>
        {
            var appearance = new AppearanceController(
                new InMemorySettingsStore(),
                new WpfAppThemeApplier(wpf.Application));
            appearance.InitializeAsync().GetAwaiter().GetResult();
            appearance.ChangeThemeAsync(AppThemeMode.Light).GetAwaiter().GetResult();
            var fileService = new NoopStageFileService();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(200),
                fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 500,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
            };
            window.SetResourceReference(
                Control.BackgroundProperty,
                "ApplicationBackgroundBrush");

            window.Show();
            try
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                CaptureWindowIfRequested(window, "stage-view-light.png");
                var layout = Assert.IsType<Grid>(view.Content);
                Assert.Collection(
                    layout.RowDefinitions,
                    row => Assert.Equal(new GridLength(24), row.Height),
                    row => Assert.Equal(new GridLength(1, GridUnitType.Star), row.Height),
                    row => Assert.Equal(new GridLength(28), row.Height));

                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                Assert.Equal(StageViewModel.PageSize, viewModel.Items.Count);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(list));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
                Assert.True(ScrollViewer.GetCanContentScroll(list));
                Assert.Equal(
                    ScrollBarVisibility.Hidden,
                    ScrollViewer.GetVerticalScrollBarVisibility(list));
                var profile = Assert.IsType<StageCardLayoutProfile>(view.CardLayoutProfile);
                Assert.Equal(6, profile.ColumnCount);
                Assert.Equal(
                    StageCardLayoutCalculator.Calculate(view.CardViewportWidth),
                    profile);
                Assert.Equal(profile.CardWidth, view.CardWidth, precision: 8);
                Assert.Equal(
                    StageCardLayoutCalculator.DefaultCardHeight,
                    view.CardHeight,
                    precision: 8);
                Assert.Equal(
                    StageCardLayoutCalculator.DefaultRowUnitHeight,
                    view.CardRowUnitHeight,
                    precision: 8);
                Assert.Equal(11, view.CardRows.Count);
                Assert.Equal(
                    viewModel.Items,
                    view.CardRows.SelectMany(static row => row.Items));

                var realized = FindVisualDescendants<ListBoxItem>(list).ToArray();
                Assert.NotEmpty(realized);
                Assert.True(realized.Length < view.CardRows.Count);
                var realizedCards = FindVisualDescendants<Border>(list)
                    .Where(static border => string.Equals(
                        border.Name,
                        "StageItemCard",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(realizedCards);
                Assert.True(realizedCards.Length < viewModel.Items.Count);
                var firstCard = Assert.Single(
                    FindVisualDescendants<Border>(realized[0]),
                    border => Equals(border.ToolTip, viewModel.Items[0].TargetValue));
                Assert.Equal(viewModel.Items[0].TargetValue, firstCard.ToolTip);
                Assert.Equal(profile.CardWidth, firstCard.ActualWidth, precision: 3);
                Assert.Equal(view.CardHeight, firstCard.ActualHeight, precision: 3);
                Assert.Equal(
                    viewModel.Items[0].AutomationName,
                    AutomationProperties.GetName(firstCard));
                Assert.Equal(new Thickness(0.5), firstCard.BorderThickness);
                Assert.Equal(new CornerRadius(9), firstCard.CornerRadius);
                Assert.NotEqual(
                    Colors.Transparent,
                    Assert.IsType<SolidColorBrush>(firstCard.Background).Color);
                Assert.NotEqual(
                    Colors.Transparent,
                    Assert.IsType<SolidColorBrush>(firstCard.BorderBrush).Color);
                var previewSurface = Assert.Single(
                    FindVisualDescendants<Border>(firstCard),
                    border => string.Equals(
                        border.Name,
                        "StageItemPreviewSurface",
                        StringComparison.Ordinal));
                Assert.Equal(new CornerRadius(9, 9, 0, 0), previewSurface.CornerRadius);
                var labelSurface = Assert.Single(
                    FindVisualDescendants<Border>(firstCard),
                    border => string.Equals(
                        border.Name,
                        "StageItemLabelSurface",
                        StringComparison.Ordinal));
                Assert.Equal(32, labelSurface.ActualHeight, precision: 3);
                Assert.Equal(
                    firstCard.ActualHeight - firstCard.BorderThickness.Top -
                        firstCard.BorderThickness.Bottom,
                    previewSurface.ActualHeight + labelSurface.ActualHeight,
                    precision: 3);
                var iconSurface = Assert.Single(
                    FindVisualDescendants<Border>(firstCard),
                    border => string.Equals(
                        border.Name,
                        "StageItemIconSurface",
                        StringComparison.Ordinal));
                Assert.Equal(40, iconSurface.ActualWidth, precision: 3);
                Assert.Equal(40, iconSurface.ActualHeight, precision: 3);
                Assert.Equal(new CornerRadius(9), iconSurface.CornerRadius);
                Assert.Equal(new Thickness(0.5), iconSurface.BorderThickness);
                var icon = Assert.Single(
                    FindVisualDescendants<TextBlock>(iconSurface),
                    text => string.Equals(
                        text.Name,
                        "StageItemGlyph",
                        StringComparison.Ordinal));
                Assert.Equal(21, icon.FontSize, precision: 3);
                var availableIconForeground = Assert.IsType<SolidColorBrush>(
                    icon.Foreground).Color;
                var name = Assert.Single(
                    FindVisualDescendants<TextBlock>(firstCard),
                    text => string.Equals(
                        text.Name,
                        "StageItemName",
                        StringComparison.Ordinal));
                Assert.Equal(10, name.FontSize, precision: 3);
                Assert.Equal(FontWeights.Medium, name.FontWeight);
                Assert.Equal(TextAlignment.Left, name.TextAlignment);
                Assert.Equal(TextWrapping.NoWrap, name.TextWrapping);
                var meta = Assert.Single(
                    FindVisualDescendants<TextBlock>(firstCard),
                    text => string.Equals(
                        text.Name,
                        "StageItemMetaText",
                        StringComparison.Ordinal));
                Assert.Equal(9, meta.FontSize, precision: 3);
                Assert.Equal(".docx", meta.Text);
                var menuButton = Assert.Single(
                    FindVisualDescendants<Button>(firstCard),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "打开中转站项目菜单",
                        StringComparison.Ordinal));
                var pinButton = Assert.Single(
                    FindVisualDescendants<Button>(firstCard),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "固定中转项目",
                        StringComparison.Ordinal));
                Assert.Same(viewModel.TogglePinnedCommand, pinButton.Command);
                Assert.Same(viewModel.Items[0], pinButton.CommandParameter);
                var pinGlyph = Assert.IsType<TextBlock>(pinButton.Content);
                var menuGlyph = Assert.IsType<TextBlock>(menuButton.Content);
                Assert.Equal("Segoe Fluent Icons", pinGlyph.FontFamily.Source);
                Assert.Equal("Segoe Fluent Icons", menuGlyph.FontFamily.Source);
                Assert.NotEqual("Segoe Fluent Icons", pinButton.FontFamily.Source);
                Assert.NotEqual("Segoe Fluent Icons", menuButton.FontFamily.Source);
                Assert.Equal(24, pinButton.ActualWidth, precision: 3);
                Assert.Equal(24, pinButton.ActualHeight, precision: 3);
                Assert.Equal(24, menuButton.ActualWidth, precision: 3);
                Assert.Equal(24, menuButton.ActualHeight, precision: 3);
                AssertVisualIsInside(pinButton, firstCard);
                AssertVisualIsInside(menuButton, firstCard);
                var hoverOverlay = Assert.Single(
                    FindVisualDescendants<Border>(firstCard),
                    border => string.Equals(
                        border.Name,
                        "StageItemHoverOverlay",
                        StringComparison.Ordinal));
                var focusRing = Assert.Single(
                    FindVisualDescendants<Border>(firstCard),
                    border => string.Equals(
                        border.Name,
                        "StageItemFocusRing",
                        StringComparison.Ordinal));
                var actionChrome = Assert.Single(
                    FindVisualDescendants<Grid>(firstCard),
                    grid => string.Equals(
                        grid.Name,
                        "StageItemActionChrome",
                        StringComparison.Ordinal));
                var cardTransforms = Assert.IsType<TransformGroup>(firstCard.RenderTransform);
                var cardScale = Assert.Single(cardTransforms.Children.OfType<ScaleTransform>());
                var cardTranslation = Assert.Single(
                    cardTransforms.Children.OfType<TranslateTransform>());
                Assert.Equal(0, hoverOverlay.Opacity, precision: 3);
                Assert.Equal(0, focusRing.Opacity, precision: 3);
                Assert.Equal(0.55, actionChrome.Opacity, precision: 3);
                Assert.Equal(1, cardScale.ScaleX, precision: 3);
                Assert.Equal(1, cardScale.ScaleY, precision: 3);
                Assert.Equal(0, cardTranslation.X, precision: 3);
                Assert.Equal(0, cardTranslation.Y, precision: 3);

                StageView.SetIsInteractionActive(firstCard, true);
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => hoverOverlay.Opacity >= 0.999 &&
                          focusRing.Opacity <= 0.001 &&
                          actionChrome.Opacity >= 0.999 &&
                          cardScale.ScaleX >= 1.019 &&
                          cardScale.ScaleY >= 1.019,
                    TimeSpan.FromSeconds(1)));
                CaptureWindowIfRequested(window, "stage-view-hover-light.png");
                StageView.SetIsInteractionActive(firstCard, false);
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => hoverOverlay.Opacity <= 0.001 &&
                          actionChrome.Opacity <= 0.551 &&
                          cardScale.ScaleX <= 1.001 &&
                          cardScale.ScaleY <= 1.001,
                    TimeSpan.FromSeconds(1)));

                Assert.True(pinButton.Focus());
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => firstCard.IsKeyboardFocusWithin &&
                          hoverOverlay.Opacity >= 0.999 &&
                          focusRing.Opacity >= 0.999 &&
                          actionChrome.Opacity >= 0.999 &&
                          cardScale.ScaleX >= 1.019 &&
                          cardScale.ScaleY >= 1.019,
                    TimeSpan.FromSeconds(1)));
                var firstContainer = Assert.IsType<ContentPresenter>(
                    FindVisualAncestor<ContentPresenter>(firstCard));
                Assert.Equal(1, Panel.GetZIndex(firstContainer));
                CaptureWindowIfRequested(window, "stage-view-keyboard-focus-light.png");
                Assert.DoesNotContain(
                    FindVisualDescendants<Border>(firstCard),
                    static border => string.Equals(
                        border.Name,
                        "StageReorderSlot",
                        StringComparison.Ordinal));
                Assert.DoesNotContain(
                    FindVisualDescendants<TextBlock>(firstCard),
                    text => string.Equals(
                        text.Text,
                        viewModel.Items[0].ParentPath,
                        StringComparison.Ordinal));
                var menu = Assert.IsType<ContextMenu>(menuButton.ContextMenu);
                Assert.Equal(6, menu.Items.Count);
                var openItem = Assert.IsType<MenuItem>(menu.Items[0]);
                var revealItem = Assert.IsType<MenuItem>(menu.Items[1]);
                var copyPathItem = Assert.IsType<MenuItem>(menu.Items[2]);
                var relinkItem = Assert.IsType<MenuItem>(menu.Items[3]);
                _ = Assert.IsType<Separator>(menu.Items[4]);
                var removeItem = Assert.IsType<MenuItem>(menu.Items[5]);
                Assert.Equal("打开", openItem.Header);
                Assert.Equal("在文件资源管理器中定位", revealItem.Header);
                Assert.Equal("复制原路径", copyPathItem.Header);
                Assert.Equal("重新关联…", relinkItem.Header);
                Assert.Equal("从中转站移除", removeItem.Header);
                menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.True(menu.IsOpen);
                Assert.True(StageView.GetIsContextMenuOpen(firstCard));
                Assert.True(StageView.GetIsContextMenuOpen(firstContainer));
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => hoverOverlay.Opacity >= 0.999 &&
                          focusRing.Opacity >= 0.999 &&
                          actionChrome.Opacity >= 0.999,
                    TimeSpan.FromSeconds(1)));
                Assert.NotEqual("Segoe Fluent Icons", menu.FontFamily.Source);
                Assert.All(
                    menu.Items.OfType<MenuItem>(),
                    item => Assert.NotEqual("Segoe Fluent Icons", item.FontFamily.Source));
                Assert.Same(viewModel.OpenCommand, openItem.Command);
                Assert.Same(viewModel.RevealCommand, revealItem.Command);
                Assert.Same(viewModel.CopyPathCommand, copyPathItem.Command);
                Assert.Same(viewModel.RelinkCommand, relinkItem.Command);
                Assert.Same(viewModel.RemoveCommand, removeItem.Command);
                Assert.Same(viewModel.Items[0], openItem.CommandParameter);
                Assert.Same(viewModel.Items[0], revealItem.CommandParameter);
                Assert.Same(viewModel.Items[0], copyPathItem.CommandParameter);
                Assert.Same(viewModel.Items[0], relinkItem.CommandParameter);
                Assert.Same(viewModel.Items[0], removeItem.CommandParameter);
                Keyboard.ClearFocus();
                view.SetExternalDragState(sessionActive: true, targeted: false);
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => !menu.IsOpen &&
                          !StageView.GetIsContextMenuOpen(firstCard) &&
                          !StageView.GetIsContextMenuOpen(firstContainer) &&
                          hoverOverlay.Opacity <= 0.001 &&
                          focusRing.Opacity <= 0.001 &&
                          actionChrome.Opacity <= 0.551 &&
                          cardScale.ScaleX <= 1.001 &&
                          cardScale.ScaleY <= 1.001,
                    TimeSpan.FromSeconds(1)));
                Assert.Equal(0, Panel.GetZIndex(firstContainer));
                view.SetExternalDragState(sessionActive: false, targeted: false);
                Assert.InRange(fileService.InspectedIds.Count, 1, realizedCards.Length);
                Assert.True(fileService.InspectedIds.Count < viewModel.Items.Count);

                Assert.True(viewModel.Items[0].ApplyAvailability(
                    viewModel.Items[0].Revision,
                    StageFileAvailability.Missing));
                window.UpdateLayout();
                Assert.False(openItem.IsEnabled);
                Assert.False(revealItem.IsEnabled);
                Assert.True(relinkItem.IsEnabled);
                Assert.True(copyPathItem.IsEnabled);
                Assert.Equal("已失效", meta.Text);
                Assert.NotEqual(
                    availableIconForeground,
                    Assert.IsType<SolidColorBrush>(icon.Foreground).Color);
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(meta.Foreground).Color,
                    Assert.IsType<SolidColorBrush>(icon.Foreground).Color);
                Assert.Contains(
                    "源文件已失效",
                    AutomationProperties.GetName(firstCard),
                    StringComparison.Ordinal);
                CaptureWindowIfRequested(window, "stage-view-missing-light.png");

                window.Width = 260;
                window.Height = 408;
                window.UpdateLayout();
                PumpLayout(window);
                Assert.Equal(260, view.ActualWidth, precision: 3);
                Assert.Equal(408, view.ActualHeight, precision: 3);
                var narrowProfile = Assert.IsType<StageCardLayoutProfile>(view.CardLayoutProfile);
                Assert.Equal(3, narrowProfile.ColumnCount);
                Assert.Equal(22, view.CardRows.Count);
                Assert.Equal(
                    viewModel.Items,
                    view.CardRows.SelectMany(static row => row.Items));
                CaptureWindowIfRequested(window, "stage-view-narrow-light.png");

                var overlay = Assert.IsType<Grid>(view.FindName("DropOverlay"));
                var wash = Assert.IsType<Border>(view.FindName("StageDropWash"));
                var outline = Assert.IsType<Border>(view.FindName("StageDropOutline"));
                var prompt = Assert.IsType<Border>(view.FindName("StageDropPrompt"));
                var promptText = Assert.IsType<TextBlock>(view.FindName("StageDropPromptText"));
                var interactionSurface = Assert.IsType<Grid>(
                    view.FindName("StageInteractionSurface"));
                Assert.False(overlay.IsHitTestVisible);
                Assert.Equal("松开以存入中转站", AutomationProperties.GetName(overlay));
                Assert.Equal("松开以存入中转站", promptText.Text);
                Assert.Equal(0.08, wash.Opacity, precision: 3);
                Assert.NotNull(wash.Background);
                Assert.Equal(new Thickness(2), outline.BorderThickness);
                Assert.Equal(new CornerRadius(0), outline.CornerRadius);
                Assert.True(outline.SnapsToDevicePixels);
                Assert.Equal(new Thickness(1), prompt.BorderThickness);
                Assert.Equal(new CornerRadius(14), prompt.CornerRadius);

                view.SetExternalDragState(sessionActive: true, targeted: false);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.False(interactionSurface.IsHitTestVisible);

                view.SetExternalDragState(sessionActive: true, targeted: true);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.False(interactionSurface.IsHitTestVisible);
                PumpLayout(window);
                CaptureWindowIfRequested(window, "stage-view-drop-targeted-light.png");

                view.SetExternalDragState(sessionActive: true, targeted: true);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.False(interactionSurface.IsHitTestVisible);

                view.SetExternalDragState(sessionActive: true, targeted: false);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.False(interactionSurface.IsHitTestVisible);

                view.SetExternalDragState(sessionActive: false, targeted: false);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.True(interactionSurface.IsHitTestVisible);

                appearance.ChangeThemeAsync(AppThemeMode.Dark).GetAwaiter().GetResult();
                window.Width = 520;
                window.Height = 500;
                window.UpdateLayout();
                PumpLayout(window);
                Assert.NotNull(firstCard.BorderBrush);
                view.SetExternalDragState(sessionActive: true, targeted: true);
                PumpLayout(window);
                CaptureWindowIfRequested(window, "stage-view-drop-targeted-dark.png");
                view.SetExternalDragState(sessionActive: false, targeted: false);
                PumpLayout(window);
                CaptureWindowIfRequested(window, "stage-view-dark.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DirectReorderKeepsCardActionsAndExternalDragCancelsThePreview()
    {
        wpf.Invoke(() =>
        {
            var fileService = new NoopStageFileService();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(12),
                fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var originalIds = viewModel.Items.Select(static item => item.Id).ToArray();
            var view = new StageView { DataContext = viewModel };
            var window = new Window
            {
                Width = 520,
                Height = 500,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.Show();
            try
            {
                PumpLayout(window);
                Assert.DoesNotContain(
                    FindVisualDescendants<Button>(view),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "进入中转站排序模式",
                        StringComparison.Ordinal) || string.Equals(
                        AutomationProperties.GetName(button),
                        "退出中转站排序模式",
                        StringComparison.Ordinal));
                var firstCard = FindVisualDescendants<Border>(view).First(border =>
                    string.Equals(border.Name, "StageItemCard", StringComparison.Ordinal));
                var actionChrome = FindVisualDescendants<Grid>(firstCard).Single(grid =>
                    string.Equals(
                        grid.Name,
                        "StageItemActionChrome",
                        StringComparison.Ordinal));
                Assert.Equal(Visibility.Visible, actionChrome.Visibility);
                var transforms = Assert.IsType<TransformGroup>(firstCard.RenderTransform);
                Assert.Single(transforms.Children.OfType<TranslateTransform>());
                var layoutChangeSource = viewModel.Items[5];
                var layoutGeneration = viewModel.BeginReorder(layoutChangeSource);
                Assert.True(viewModel.PreviewReorder(layoutGeneration, 0));
                Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));
                window.Width = 260;
                PumpLayout(window);
                Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));
                Assert.False(viewModel.IsReordering);
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    FindVisualDescendants<ScrollViewer>(
                        Assert.IsType<ListBox>(view.FindName("StageItemsList"))).Single());
                Assert.Equal(0, scrollViewer.VerticalOffset, precision: 3);

                CaptureWindowIfRequested(window, "stage-direct-reorder-narrow-light.png");

                var source = viewModel.Items[5];
                var generation = viewModel.BeginReorder(source);
                Assert.True(viewModel.PreviewReorder(generation, 0));
                PumpLayout(window);
                Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));

                view.SetExternalDragState(sessionActive: true, targeted: true);
                PumpLayout(window);

                Assert.Equal(originalIds, viewModel.Items.Select(static item => item.Id));
                Assert.False(viewModel.IsReordering);
                Assert.False(view.HasActiveReorderPointer);
                Assert.False(view.HasAutoScrollSubscription);
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Grid>(view.FindName("DropOverlay")).Visibility);

                view.SetExternalDragState(sessionActive: false, targeted: false);
                PumpLayout(window);
                Assert.Equal(Visibility.Visible, actionChrome.Visibility);
                Assert.True(fileService.InspectedIds.Count <= viewModel.Items.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HiddenViewCancelsVisibleChecksAndRejectsTheirLateResults()
    {
        wpf.Invoke(() =>
        {
            var fileService = new DeferredStageFileService();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(20),
                fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.Show();
            try
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.NotEmpty(fileService.Requests);
                Assert.True(fileService.Requests.Count < viewModel.Items.Count);

                window.Hide();
                Assert.All(fileService.Requests, request =>
                    Assert.True(request.CancellationToken.IsCancellationRequested));
                foreach (var request in fileService.Requests)
                {
                    request.Completion.TrySetResult(StageFileAvailability.Missing);
                }

                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.All(viewModel.Items, item =>
                    Assert.False(item.HasAvailabilityStatus));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AuthoritativePinRevisionRebindsTheVisibleAvailabilityCheck()
    {
        wpf.Invoke(() =>
        {
            var repository = new StaticStageRepository(20);
            var fileService = new DeferredStageFileService();
            using var viewModel = new StageViewModel(repository, fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.Show();
            try
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                var projection = viewModel.Items[0];
                var originalRequest = Assert.Single(
                    fileService.Requests,
                    request => request.Item.Id == projection.Id);
                originalRequest.Completion.SetResult(StageFileAvailability.Missing);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.True(projection.IsMissing);

                viewModel.TogglePinnedCommand.ExecuteAsync(projection).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(DispatcherPriority.Loaded, static () => { });
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

                Assert.True(projection.IsPinned);
                Assert.Equal(2, projection.Revision);
                Assert.False(projection.HasAvailabilityStatus);
                Assert.Contains(
                    fileService.Requests,
                    request => request.Item.Id == projection.Id &&
                        request.Item.Revision == projection.Revision);
                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var card = Assert.Single(
                    FindVisualDescendants<Border>(list),
                    border => Equals(border.ToolTip, projection.TargetValue));
                Assert.Contains(
                    FindVisualDescendants<Button>(card),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "取消固定中转项目",
                        StringComparison.Ordinal));
                var pinnedButton = Assert.Single(
                    FindVisualDescendants<Button>(card),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "取消固定中转项目",
                        StringComparison.Ordinal));
                Assert.NotEqual(
                    Colors.Transparent,
                    Assert.IsType<SolidColorBrush>(pinnedButton.Foreground).Color);
                CaptureWindowIfRequested(window, "stage-view-pinned-light.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(260, 3)]
    [InlineData(520, 6)]
    [InlineData(720, 9)]
    [InlineData(800, 10)]
    [InlineData(960, 10)]
    public void StageUsesTheFrozenCardGeometryFromItsRealScrollViewport(
        double outerWidth,
        int expectedColumns)
    {
        wpf.Invoke(() =>
        {
            var appearance = new AppearanceController(
                new InMemorySettingsStore(),
                new WpfAppThemeApplier(wpf.Application));
            appearance.InitializeAsync().GetAwaiter().GetResult();
            appearance.ChangeThemeAsync(AppThemeMode.Light).GetAwaiter().GetResult();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(11),
                new NoopStageFileService(item => item.ResolvedName switch
                {
                    "Visual Studio 2026 Preview.lnk" => StageFileAvailability.Missing,
                    "Pinned-and-missing-reference.txt" => StageFileAvailability.Missing,
                    "暂时无法访问的网络资料" => StageFileAvailability.Unavailable,
                    "状态尚未确定的移动设备文件.bin" => StageFileAvailability.Unknown,
                    _ => StageFileAvailability.Available,
                }));
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = outerWidth,
                Height = 500,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
            };
            window.SetResourceReference(
                Control.BackgroundProperty,
                "ApplicationBackgroundBrush");

            window.Show();
            try
            {
                PumpLayout(window);
                var profile = Assert.IsType<StageCardLayoutProfile>(view.CardLayoutProfile);
                Assert.True(view.CardViewportWidth > 0);
                Assert.True(view.CardViewportWidth < outerWidth);
                Assert.Equal(outerWidth - 16, view.CardViewportWidth, precision: 3);
                Assert.Equal(
                    StageCardLayoutCalculator.Calculate(view.CardViewportWidth),
                    profile);
                Assert.Equal(expectedColumns, profile.ColumnCount);
                Assert.Equal(profile.CardWidth, view.CardWidth, precision: 8);
                Assert.Equal(
                    StageCardLayoutCalculator.DefaultCardHeight,
                    view.CardHeight,
                    precision: 8);
                Assert.Equal(
                    StageCardLayoutCalculator.DefaultRowUnitHeight,
                    view.CardRowUnitHeight,
                    precision: 8);
                Assert.Equal(
                    profile.ColumnCount == 1
                        ? profile.CardWidth
                        : profile.ColumnCount *
                            (profile.CardWidth + profile.HorizontalGap),
                    view.CardRowSurfaceWidth,
                    precision: 8);
                Assert.Equal(
                    (int)Math.Ceiling(11.0 / expectedColumns),
                    view.CardRows.Count);
                Assert.Equal(
                    viewModel.Items,
                    view.CardRows.SelectMany(static row => row.Items));
                Assert.All(view.CardRows, row =>
                {
                    Assert.Equal(expectedColumns, row.ColumnCount);
                    Assert.InRange(row.Items.Count, 1, expectedColumns);
                });
                var expectedTail = 11 % expectedColumns;
                Assert.Equal(
                    expectedTail == 0 ? expectedColumns : expectedTail,
                    view.CardRows[^1].Items.Count);

                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var firstRow = FindVisualDescendants<ListBoxItem>(list)
                    .OrderBy(item => item.TransformToAncestor(list).Transform(new Point()).Y)
                    .First();
                var cards = FindVisualDescendants<Border>(firstRow)
                    .Where(static border => string.Equals(
                        border.Name,
                        "StageItemCard",
                        StringComparison.Ordinal))
                    .OrderBy(card => card.TransformToAncestor(list).Transform(new Point()).X)
                    .ToArray();
                Assert.Equal(Math.Min(expectedColumns, 11), cards.Length);
                Assert.All(cards, card =>
                {
                    Assert.Equal(profile.CardWidth, card.ActualWidth, precision: 3);
                    Assert.Equal(view.CardHeight, card.ActualHeight, precision: 3);
                    Assert.Equal(
                        Assert.IsType<StageItemViewModel>(card.DataContext).TargetValue,
                        card.ToolTip);
                    Assert.False(string.IsNullOrWhiteSpace(
                        AutomationProperties.GetName(card)));
                    var buttons = FindVisualDescendants<Button>(card).ToArray();
                    Assert.Equal(2, buttons.Length);
                    Assert.All(buttons, button => AssertVisualIsInside(button, card));
                    var iconSurface = Assert.Single(
                        FindVisualDescendants<Border>(card),
                        border => string.Equals(
                            border.Name,
                            "StageItemIconSurface",
                            StringComparison.Ordinal));
                    Assert.Equal(40, iconSurface.ActualWidth, precision: 3);
                    Assert.Equal(40, iconSurface.ActualHeight, precision: 3);
                    AssertVisualIsInside(iconSurface, card);
                    var name = Assert.Single(
                        FindVisualDescendants<TextBlock>(card),
                        text => string.Equals(
                            text.Name,
                            "StageItemName",
                            StringComparison.Ordinal));
                    AssertVisualIsInside(name, card);
                    var meta = Assert.Single(
                        FindVisualDescendants<TextBlock>(card),
                        text => string.Equals(
                            text.Name,
                            "StageItemMetaText",
                            StringComparison.Ordinal));
                    AssertVisualIsInside(meta, card);
                });
                var pinnedMissingCard = Assert.Single(
                    FindVisualDescendants<Border>(list),
                    border => border.DataContext is StageItemViewModel item &&
                        string.Equals(
                            item.DisplayName,
                            "Pinned-and-missing-reference.txt",
                            StringComparison.Ordinal) &&
                        string.Equals(
                            border.Name,
                            "StageItemCard",
                            StringComparison.Ordinal));
                var pinnedMissingMeta = Assert.Single(
                    FindVisualDescendants<TextBlock>(pinnedMissingCard),
                    text => string.Equals(
                        text.Name,
                        "StageItemMetaText",
                        StringComparison.Ordinal));
                Assert.Equal("已失效", pinnedMissingMeta.Text);

                for (var index = 1; index < cards.Length; index++)
                {
                    var previous = cards[index - 1]
                        .TransformToAncestor(list)
                        .TransformBounds(new Rect(cards[index - 1].RenderSize));
                    var current = cards[index]
                        .TransformToAncestor(list)
                        .TransformBounds(new Rect(cards[index].RenderSize));
                    Assert.Equal(profile.HorizontalGap, current.Left - previous.Right, precision: 3);
                }

                Assert.IsType<DataTemplate>(view.Resources["StageCardContentTemplate"]);
                CaptureWindowIfRequested(
                    window,
                    $"stage-view-{outerWidth:0}-light.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ColumnChangeKeepsTheTopVisibleItemAndRejectsOldAvailabilityResults()
    {
        wpf.Invoke(() =>
        {
            var fileService = new DeferredStageFileService();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(200),
                fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
            };

            window.Show();
            try
            {
                PumpLayout(window);
                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var scrollViewer = Assert.Single(
                    FindVisualDescendants<ScrollViewer>(list));
                scrollViewer.ScrollToVerticalOffset((3 * view.CardRowUnitHeight) + 17);
                PumpLayout(window);
                var anchor = GetTopVisibleCard(list, scrollViewer);
                var originalRequests = fileService.Requests.ToArray();
                Assert.NotEmpty(originalRequests);

                window.Width = 260;
                PumpLayout(window);

                Assert.Equal(3, view.CardLayoutProfile?.ColumnCount);
                Assert.All(originalRequests, request =>
                    Assert.True(request.CancellationToken.IsCancellationRequested));
                var visibleIds = GetVisibleCards(list, scrollViewer)
                    .Select(static card => card.Item.Id)
                    .ToArray();
                Assert.Contains(anchor.Item.Id, visibleIds);
                foreach (var request in originalRequests)
                {
                    request.Completion.TrySetResult(StageFileAvailability.Missing);
                }

                PumpLayout(window);
                Assert.All(
                    originalRequests.Select(static request => request.Item.Id).Distinct(),
                    id => Assert.False(viewModel.Items.Single(item => item.Id == id).IsMissing));
            }
            finally
            {
                window.Close();
            }

            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            Assert.Equal(0, view.ActiveAvailabilityBindingCount);
            Assert.Empty(view.CardRows);
        });
    }

    [Fact]
    public void CleaningARecycledRowCancelsEveryCardRequestInThatRow()
    {
        wpf.Invoke(() =>
        {
            var fileService = new DeferredStageFileService();
            using var viewModel = new StageViewModel(
                new StaticStageRepository(200),
                fileService);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.Show();
            try
            {
                PumpLayout(window);
                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var rowContainer = FindVisualDescendants<ListBoxItem>(list).First();
                var rowItems = Assert.IsType<StageCardRowViewModel>(rowContainer.DataContext).Items;
                Assert.Equal(6, rowItems.Count);
                var rowRequests = rowItems
                    .Select(item => Assert.Single(
                        fileService.Requests,
                        request => request.Item.Id == item.Id))
                    .ToArray();

                view.CancelAvailabilityBindingsForRow(rowContainer);

                Assert.All(rowRequests, request =>
                    Assert.True(request.CancellationToken.IsCancellationRequested));
                foreach (var request in rowRequests)
                {
                    request.Completion.TrySetResult(StageFileAvailability.Missing);
                }

                PumpLayout(window);
                Assert.All(rowItems, item => Assert.False(item.HasAvailabilityStatus));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RepeatedResizeScrollAndVisibilityCyclesLeaveNoDeferredCardWork()
    {
        wpf.Invoke(() =>
        {
            using var viewModel = new StageViewModel(
                new StaticStageRepository(200),
                new NoopStageFileService());
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = viewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
            };

            window.Show();
            try
            {
                PumpLayout(window);
                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var scrollViewer = Assert.Single(
                    FindVisualDescendants<ScrollViewer>(list));
                for (var cycle = 0; cycle < 100; cycle++)
                {
                    scrollViewer.ScrollToVerticalOffset(
                        (cycle * 37.5) % Math.Max(1, scrollViewer.ScrollableHeight));
                    window.Width = cycle % 2 == 0 ? 260 : 520;
                    PumpLayout(window);

                    var expectedColumns = cycle % 2 == 0 ? 3 : 6;
                    Assert.Equal(expectedColumns, view.CardLayoutProfile?.ColumnCount);
                    Assert.Equal(
                        viewModel.Items,
                        view.CardRows.SelectMany(static row => row.Items));
                    Assert.Equal(
                        (int)Math.Ceiling(
                            (double)viewModel.Items.Count / expectedColumns),
                        view.CardRows.Count);
                    var realizedCards = FindVisualDescendants<Border>(list).Count(
                        static border => string.Equals(
                            border.Name,
                            "StageItemCard",
                            StringComparison.Ordinal));
                    Assert.InRange(view.TrackedAvailabilityCardCount, 1, realizedCards);
                    Assert.InRange(view.ActiveAvailabilityBindingCount, 1, realizedCards);
                    Assert.False(view.HasPendingDeferredWork);

                    var interactionCard = FindVisualDescendants<Border>(list).First(
                        static border => string.Equals(
                            border.Name,
                            "StageItemCard",
                            StringComparison.Ordinal));
                    var interactionButton = FindVisualDescendants<Button>(interactionCard).First();
                    Assert.True(interactionButton.Focus());
                    Assert.True(StageView.GetIsInteractionActive(interactionCard));
                    Assert.True(StageView.GetIsKeyboardContextActive(interactionCard));
                    view.SetExternalDragState(sessionActive: true, targeted: false);
                    Assert.False(StageView.GetIsInteractionActive(interactionCard));
                    Assert.False(StageView.GetIsKeyboardContextActive(interactionCard));
                    Keyboard.ClearFocus();
                    view.SetExternalDragState(sessionActive: false, targeted: false);

                    if ((cycle + 1) % 10 == 0)
                    {
                        window.Hide();
                        window.Dispatcher.Invoke(
                            DispatcherPriority.ApplicationIdle,
                            static () => { });
                        Assert.Equal(0, view.ActiveAvailabilityBindingCount);
                        Assert.All(
                            FindVisualDescendants<Border>(list).Where(
                                static border => string.Equals(
                                    border.Name,
                                    "StageItemCard",
                                    StringComparison.Ordinal)),
                            card =>
                            {
                                Assert.False(StageView.GetIsInteractionActive(card));
                                Assert.False(StageView.GetIsKeyboardContextActive(card));
                                Assert.False(StageView.GetIsContextMenuOpen(card));
                            });
                        window.Show();
                        PumpLayout(window);
                    }
                }

                view.SetExternalDragState(sessionActive: true, targeted: false);
                Assert.True(WaitForDispatcherCondition(
                    window.Dispatcher,
                    () => FindVisualDescendants<Border>(list)
                        .Where(static border => string.Equals(
                            border.Name,
                            "StageItemCard",
                            StringComparison.Ordinal))
                        .All(IsCardAtRest),
                    TimeSpan.FromSeconds(1)));
                view.SetExternalDragState(sessionActive: false, targeted: false);
            }
            finally
            {
                window.Close();
            }

            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            Assert.Equal(0, view.ActiveAvailabilityBindingCount);
            Assert.Equal(0, view.TrackedAvailabilityCardCount);
            Assert.Empty(view.CardRows);
            Assert.False(view.HasPendingDeferredWork);
        });
    }

    [Fact]
    public void DataContextAndPageReplacementPublishOnlyTheCurrentRowsThenScrollTop()
    {
        wpf.Invoke(() =>
        {
            using var oldViewModel = new StageViewModel(
                new StaticStageRepository(200),
                new NoopStageFileService());
            using var currentViewModel = new StageViewModel(
                new StaticStageRepository(200),
                new NoopStageFileService());
            oldViewModel.InitializeAsync().GetAwaiter().GetResult();
            currentViewModel.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView
            {
                DataContext = oldViewModel,
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            window.Show();
            try
            {
                PumpLayout(window);
                view.DataContext = currentViewModel;
                PumpLayout(window);
                var currentIds = currentViewModel.Items.Select(static item => item.Id).ToArray();
                Assert.Equal(
                    currentIds,
                    view.CardRows
                        .SelectMany(static row => row.Items)
                        .Select(static item => item.Id));

                oldViewModel.Items.Clear();
                oldViewModel.Items.Add(CreateStageItemViewModel(900));
                PumpLayout(window);
                Assert.Equal(
                    currentIds,
                    view.CardRows
                        .SelectMany(static row => row.Items)
                        .Select(static item => item.Id));

                var list = Assert.IsType<ListBox>(view.FindName("StageItemsList"));
                var scrollViewer = Assert.Single(
                    FindVisualDescendants<ScrollViewer>(list));
                scrollViewer.ScrollToEnd();
                PumpLayout(window);
                Assert.True(scrollViewer.VerticalOffset > 0);
                var replacement = Enumerable.Range(1000, 9)
                    .Select(CreateStageItemViewModel)
                    .ToArray();

                currentViewModel.Items.Clear();
                foreach (var item in replacement)
                {
                    currentViewModel.Items.Add(item);
                }

                Assert.Empty(view.CardRows);
                PumpLayout(window);

                Assert.Equal(0, scrollViewer.VerticalOffset, precision: 3);
                Assert.Equal(
                    replacement,
                    view.CardRows.SelectMany(static row => row.Items));
                Assert.Equal(2, view.CardRows.Count);
                Assert.False(view.HasPendingDeferredWork);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static StageItemViewModel CreateStageItemViewModel(int index)
    {
        var now = DateTimeOffset.UtcNow;
        var target = $@"C:\Stage\Replacement-{index}.txt";
        return new StageItemViewModel(new StageItem(
            Guid.NewGuid(),
            StageItemKind.File,
            target,
            target.ToUpperInvariant(),
            $"Replacement {index}",
            index,
            false,
            1,
            now,
            now));
    }

    private static VisibleCard GetTopVisibleCard(
        ListBox list,
        ScrollViewer scrollViewer) =>
        GetVisibleCards(list, scrollViewer)
            .OrderBy(static card => card.Bounds.Top)
            .ThenBy(static card => card.Bounds.Left)
            .First();

    private static IEnumerable<VisibleCard> GetVisibleCards(
        ListBox list,
        ScrollViewer scrollViewer)
    {
        foreach (var card in FindVisualDescendants<Border>(list).Where(
                     static border => string.Equals(
                         border.Name,
                         "StageItemCard",
                         StringComparison.Ordinal)))
        {
            if (card.DataContext is not StageItemViewModel item)
            {
                continue;
            }

            var bounds = card
                .TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(card.RenderSize));
            if (bounds.Bottom > 0 && bounds.Top < scrollViewer.ViewportHeight)
            {
                yield return new VisibleCard(item, bounds);
            }
        }
    }

    private sealed record VisibleCard(StageItemViewModel Item, Rect Bounds);

    private static void PumpLayout(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Loaded, static () => { });
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, static () => { });
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
    }

    private static void AssertVisualIsInside(
        FrameworkElement child,
        FrameworkElement ancestor)
    {
        var bounds = child
            .TransformToAncestor(ancestor)
            .TransformBounds(new Rect(child.RenderSize));
        const double tolerance = 0.01;
        Assert.True(bounds.Left >= -tolerance);
        Assert.True(bounds.Top >= -tolerance);
        Assert.True(bounds.Right <= ancestor.ActualWidth + tolerance);
        Assert.True(bounds.Bottom <= ancestor.ActualHeight + tolerance);
    }

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

    private static T? FindVisualAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while ((current = VisualTreeHelper.GetParent(current)) is not null)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool WaitForDispatcherCondition(
        Dispatcher dispatcher,
        Func<bool> predicate,
        TimeSpan timeout)
    {
        if (predicate())
        {
            return true;
        }

        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (predicate() || elapsed.Elapsed >= timeout)
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return predicate();
    }

    private static bool IsCardAtRest(Border card)
    {
        var overlay = FindVisualDescendants<Border>(card).SingleOrDefault(
            border => string.Equals(
                border.Name,
                "StageItemHoverOverlay",
                StringComparison.Ordinal));
        var focusRing = FindVisualDescendants<Border>(card).SingleOrDefault(
            border => string.Equals(
                border.Name,
                "StageItemFocusRing",
                StringComparison.Ordinal));
        var actionChrome = FindVisualDescendants<Grid>(card).SingleOrDefault(
            grid => string.Equals(
                grid.Name,
                "StageItemActionChrome",
                StringComparison.Ordinal));
        var transforms = card.RenderTransform as TransformGroup;
        var scale = transforms?.Children.OfType<ScaleTransform>().SingleOrDefault();
        var translation = transforms?.Children.OfType<TranslateTransform>().SingleOrDefault();
        return overlay is not null && focusRing is not null && actionChrome is not null &&
            scale is not null && translation is not null &&
            !StageView.GetIsInteractionActive(card) &&
            !StageView.GetIsKeyboardContextActive(card) &&
            overlay.Opacity <= 0.001 &&
            focusRing.Opacity <= 0.001 &&
            actionChrome.Opacity <= 0.551 &&
            scale.ScaleX <= 1.001 &&
            scale.ScaleY <= 1.001 &&
            Math.Abs(translation.X) <= 0.001 &&
            Math.Abs(translation.Y) <= 0.001;
    }

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

    private sealed class StaticStageRepository : IStageRepository
    {
        private static readonly string[] RepresentativeNames =
        [
            "季度交付报告-最终确认版本.docx",
            "设计素材与产品截图",
            "Visual Studio 2026 Preview.lnk",
            "Pinned-and-missing-reference.txt",
            "暂时无法访问的网络资料",
            "状态尚未确定的移动设备文件.bin",
            "averylongunbrokenlatinfilenamewithoutspaces.extension",
            "组合字符-e\u0301-emoji-📦-多语言-資料.txt",
            "C 盘根目录",
            "常用工作区入口.lnk",
            "会议记录 2026-09-06.md",
        ];

        private readonly StageItem[] _items;

        public StaticStageRepository(int count)
        {
            var now = DateTimeOffset.UtcNow;
            _items = Enumerable.Range(0, count)
                .Select(index =>
                {
                    var kind = (index % 3) switch
                    {
                        1 => StageItemKind.Directory,
                        2 => StageItemKind.ShellLink,
                        _ => StageItemKind.File,
                    };
                    var displayName = RepresentativeNames[index % RepresentativeNames.Length];
                    if (index >= RepresentativeNames.Length)
                    {
                        displayName = $"{displayName} {index + 1}";
                    }

                    var target = $@"C:\A very long Stage folder\{index + 1:D2}-{displayName}";
                    return new StageItem(
                        Guid.NewGuid(),
                        kind,
                        target,
                        target.ToUpperInvariant(),
                        displayName,
                        count - index,
                        index is 1 or 3 or 9,
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
                new StagePageCursor(page[^1].SortOrder, page[^1].Id),
                _items.Length));
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
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Array.FindIndex(_items, item => item.Id == itemId);
            if (index < 0)
            {
                return Task.FromResult(new StageItemMutationResult(
                    StageItemMutationStatus.NotFound,
                    null));
            }

            var item = _items[index];
            if (item.Revision != expectedRevision)
            {
                return Task.FromResult(new StageItemMutationResult(
                    StageItemMutationStatus.RevisionConflict,
                    null));
            }

            if (item.IsPinned == isPinned)
            {
                return Task.FromResult(new StageItemMutationResult(
                    StageItemMutationStatus.NoChange,
                    item));
            }

            var updated = item with
            {
                IsPinned = isPinned,
                Revision = checked(item.Revision + 1),
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            _items[index] = updated;
            return Task.FromResult(new StageItemMutationResult(
                StageItemMutationStatus.Applied,
                updated));
        }

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
        private readonly Func<StageItem, StageFileAvailability> _availability;

        public NoopStageFileService(
            Func<StageItem, StageFileAvailability>? availability = null)
        {
            _availability = availability ?? (_ => StageFileAvailability.Available);
        }

        public HashSet<Guid> InspectedIds { get; } = [];

        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StageTargetResolution>>([]);

        public Task<StageFileAvailability> InspectAsync(
            StageItem item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectedIds.Add(item.Id);
            return Task.FromResult(_availability(item));
        }

        public Task OpenAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevealInExplorerAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DeferredStageFileService : IStageFileService
    {
        public List<InspectionRequest> Requests { get; } = [];

        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StageTargetResolution>>([]);

        public Task<StageFileAvailability> InspectAsync(
            StageItem item,
            CancellationToken cancellationToken = default)
        {
            var request = new InspectionRequest(item, cancellationToken);
            Requests.Add(request);
            return request.Completion.Task;
        }

        public Task OpenAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevealInExplorerAsync(
            StageItem item,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record InspectionRequest(
        StageItem Item,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource<StageFileAvailability> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InMemorySettingsStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.Default);

        public Task SaveThemeModeAsync(
            AppThemeMode themeMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveHotkeyGestureAsync(
            Vok.Core.Platform.GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
