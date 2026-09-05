using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

                var realized = FindVisualDescendants<ListBoxItem>(list).ToArray();
                Assert.NotEmpty(realized);
                Assert.True(realized.Length < viewModel.Items.Count);
                var firstCard = Assert.Single(
                    FindVisualDescendants<Border>(realized[0]),
                    border => Equals(border.ToolTip, viewModel.Items[0].TargetValue));
                Assert.Equal(viewModel.Items[0].TargetValue, firstCard.ToolTip);
                Assert.Equal(
                    viewModel.Items[0].AutomationName,
                    AutomationProperties.GetName(firstCard));
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
                var menu = Assert.IsType<ContextMenu>(menuButton.ContextMenu);
                Assert.Equal(5, menu.Items.Count);
                var openItem = Assert.IsType<MenuItem>(menu.Items[0]);
                var revealItem = Assert.IsType<MenuItem>(menu.Items[1]);
                var copyPathItem = Assert.IsType<MenuItem>(menu.Items[2]);
                _ = Assert.IsType<Separator>(menu.Items[3]);
                var removeItem = Assert.IsType<MenuItem>(menu.Items[4]);
                Assert.Equal("打开", openItem.Header);
                Assert.Equal("在文件资源管理器中定位", revealItem.Header);
                Assert.Equal("复制原路径", copyPathItem.Header);
                Assert.Equal("从中转站移除", removeItem.Header);
                menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.True(menu.IsOpen);
                Assert.Same(viewModel.OpenCommand, openItem.Command);
                Assert.Same(viewModel.RevealCommand, revealItem.Command);
                Assert.Same(viewModel.CopyPathCommand, copyPathItem.Command);
                Assert.Same(viewModel.RemoveCommand, removeItem.Command);
                Assert.Same(viewModel.Items[0], openItem.CommandParameter);
                Assert.Same(viewModel.Items[0], revealItem.CommandParameter);
                Assert.Same(viewModel.Items[0], copyPathItem.CommandParameter);
                Assert.Same(viewModel.Items[0], removeItem.CommandParameter);
                menu.IsOpen = false;
                Assert.InRange(fileService.InspectedIds.Count, 1, realized.Length);
                Assert.True(fileService.InspectedIds.Count < viewModel.Items.Count);

                Assert.True(viewModel.Items[0].ApplyAvailability(
                    viewModel.Items[0].Revision,
                    StageFileAvailability.Missing));
                window.UpdateLayout();
                Assert.False(openItem.IsEnabled);
                Assert.False(revealItem.IsEnabled);
                Assert.True(copyPathItem.IsEnabled);
                var missingText = Assert.Single(
                    FindVisualDescendants<TextBlock>(firstCard),
                    text => string.Equals(
                        text.Text,
                        "源文件已失效",
                        StringComparison.Ordinal));
                Assert.Equal(Visibility.Visible, missingText.Visibility);
                Assert.Contains(
                    "源文件已失效",
                    AutomationProperties.GetName(firstCard),
                    StringComparison.Ordinal);
                CaptureWindowIfRequested(window, "stage-view-missing-light.png");

                window.Width = 260;
                window.Height = 408;
                window.UpdateLayout();
                Assert.Equal(260, view.ActualWidth, precision: 3);
                Assert.Equal(408, view.ActualHeight, precision: 3);
                CaptureWindowIfRequested(window, "stage-view-narrow-light.png");

                var overlay = Assert.IsType<Border>(view.FindName("DropOverlay"));
                var interactionSurface = Assert.IsType<Grid>(
                    view.FindName("StageInteractionSurface"));
                view.SetExternalDragState(sessionActive: true, targeted: false);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.False(interactionSurface.IsHitTestVisible);
                view.SetExternalDragState(sessionActive: true, targeted: true);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.Equal(new Thickness(2), overlay.BorderThickness);
                Assert.Equal(new CornerRadius(0), overlay.CornerRadius);
                view.SetExternalDragState(sessionActive: false, targeted: false);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.True(interactionSurface.IsHitTestVisible);

                appearance.ChangeThemeAsync(AppThemeMode.Dark).GetAwaiter().GetResult();
                window.Width = 520;
                window.Height = 500;
                window.UpdateLayout();
                Assert.NotNull(firstCard.BorderBrush);
                CaptureWindowIfRequested(window, "stage-view-dark.png");
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
                var pinnedText = Assert.Single(
                    FindVisualDescendants<TextBlock>(card),
                    text => string.Equals(text.Text, "已固定", StringComparison.Ordinal));
                Assert.Equal(Visibility.Visible, pinnedText.Visibility);
                CaptureWindowIfRequested(window, "stage-view-pinned-light.png");
            }
            finally
            {
                window.Close();
            }
        });
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
        private readonly StageItem[] _items;

        public StaticStageRepository(int count)
        {
            var now = DateTimeOffset.UtcNow;
            _items = Enumerable.Range(0, count)
                .Select(index => new StageItem(
                    Guid.NewGuid(),
                    index % 2 == 0 ? StageItemKind.File : StageItemKind.Directory,
                    $@"C:\A very long Stage folder\Item-{index + 1}.txt",
                    $@"C:\A VERY LONG STAGE FOLDER\ITEM-{index + 1}.TXT",
                    $"中转项目 {index + 1}",
                    count - index,
                    false,
                    1,
                    now,
                    now))
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
    }

    private sealed class NoopStageFileService : IStageFileService
    {
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
            return Task.FromResult(StageFileAvailability.Available);
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
