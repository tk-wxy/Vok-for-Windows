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

                window.Width = 260;
                window.Height = 408;
                window.UpdateLayout();
                Assert.Equal(260, view.ActualWidth, precision: 3);
                Assert.Equal(408, view.ActualHeight, precision: 3);

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
        private readonly IReadOnlyList<StageItem> _items;

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
                _items.Count));
        }

        public Task<StageAddOrMoveResult> AddOrMoveAsync(
            IReadOnlyList<StageTargetDescriptor> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopStageFileService : IStageFileService
    {
        public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StageTargetResolution>>([]);
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
