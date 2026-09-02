using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

[Collection(WpfApplicationCollection.Name)]
public sealed class ClipboardViewRecyclingTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void ClearHistoryActionFitsTheFixedHeaderAndUsesTheViewModelCommand()
    {
        wpf.Invoke(() =>
        {
            using var viewModel = new ClipboardViewModel(new NoopClipboardRepository());
            viewModel.ApplyCaptured(new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Text,
                new string('a', 64),
                "clear",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                1,
                1,
                false,
                1));
            var view = new ClipboardView { DataContext = viewModel };
            view.Measure(new Size(420, 320));
            view.Arrange(new Rect(0, 0, 420, 320));
            view.UpdateLayout();

            var button = Assert.IsType<Button>(view.FindName("ClearHistoryButton"));
            Assert.Equal(20, button.ActualHeight);
            Assert.Same(viewModel.ClearHistoryCommand, button.Command);
            Assert.Equal("清空", button.Content);
            Assert.True(button.IsEnabled);
        });
    }

    [Fact]
    public void OnlyViewportImageContainersLoadAndWindowHideReleasesSources()
    {
        wpf.Invoke(() =>
        {
            var cache = new ImmediateThumbnailCache(CreateFrozenImage(10, 10));
            using var viewModel = new ClipboardViewModel(
                new NoopClipboardRepository(),
                filePresentation: null,
                cache,
                imageAssets: null);
            AddImages(viewModel, 80);
            var view = new ClipboardView { DataContext = viewModel };
            var window = new Window
            {
                Width = 420,
                Height = 520,
                Content = view,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var loaded = PumpDispatcherUntil(
                    window.Dispatcher,
                    () => cache.LoadCount > 0,
                    TimeSpan.FromSeconds(1));
                Assert.True(loaded, DescribeThumbnailLayout(view));

                var realized = FindThumbnailImages(view);
                Assert.NotEmpty(realized);
                Assert.True(realized.Count < 80);
                Assert.InRange(cache.LoadCount, 1, realized.Count);
                Assert.Contains(realized, image => image.Source is not null);
                Assert.All(viewModel.Items, item => Assert.Null(item.Icon));

                window.Hide();
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));

                Assert.All(realized, image => Assert.Null(image.Source));
                Assert.All(
                    viewModel.Items,
                    item => Assert.Equal(
                        ClipboardThumbnailDisplayState.NotRequested,
                        item.ThumbnailState));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RecycledContainerRejectsACompletedResultFromItsPreviousItem()
    {
        wpf.Invoke(() =>
        {
            var staleImage = CreateFrozenImage(11, 7);
            var cache = new DelayedThumbnailCache(staleImage);
            using var viewModel = new ClipboardViewModel(
                new NoopClipboardRepository(),
                filePresentation: null,
                cache,
                imageAssets: null);
            AddImages(viewModel, 80);
            var view = new ClipboardView { DataContext = viewModel };
            var window = new Window
            {
                Width = 420,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var loaded = PumpDispatcherUntil(
                    window.Dispatcher,
                    () => cache.Requests.Count > 0,
                    TimeSpan.FromSeconds(1));
                Assert.True(loaded, DescribeThumbnailLayout(view));
                var first = cache.Requests[0];
                var initialCount = cache.Requests.Count;
                var list = Assert.IsType<ListBox>(view.FindName("ClipboardItemsList"));
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    FindVisualDescendant<ScrollViewer>(list));

                scrollViewer.ScrollToEnd();
                window.UpdateLayout();
                Assert.True(PumpDispatcherUntil(
                    window.Dispatcher,
                    () => first.CancellationToken.IsCancellationRequested &&
                          cache.Requests.Count > initialCount,
                    TimeSpan.FromSeconds(1)));

                first.Complete();
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));

                Assert.DoesNotContain(
                    FindThumbnailImages(view),
                    image => ReferenceEquals(image.Source, staleImage));
            }
            finally
            {
                window.Hide();
                cache.CancelAll();
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
                window.Close();
            }
        });
    }

    private static void AddImages(ClipboardViewModel viewModel, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var thumbnailHash = (index + 1).ToString("x64");
            viewModel.ApplyCaptured(new ClipboardEntry(
                Guid.NewGuid(),
                ClipboardContentKind.Image,
                (index + count + 1).ToString("x64"),
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                index + 1,
                1,
                false,
                1,
                Image: new ClipboardImageMetadata(
                    640 + index,
                    360 + index,
                    true,
                    96000,
                    96000,
                    new ClipboardAssetReference(
                        (index + count * 2 + 1).ToString("x64"),
                        ClipboardAssetState.Ready,
                        1),
                    new ClipboardAssetReference(
                        thumbnailHash,
                        ClipboardAssetState.Ready,
                        1))));
        }
    }

    private static BitmapSource CreateFrozenImage(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 20;
            pixels[index + 1] = 90;
            pixels[index + 2] = 180;
            pixels[index + 3] = 160;
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

    private static List<Image> FindThumbnailImages(DependencyObject root) =>
        FindVisualDescendants<Image>(root)
            .Where(image => string.Equals(
                image.Name,
                "ThumbnailImage",
                StringComparison.Ordinal))
            .ToList();

    private static string DescribeThumbnailLayout(ClipboardView view)
    {
        var images = FindThumbnailImages(view);
        var list = Assert.IsType<ListBox>(view.FindName("ClipboardItemsList"));
        var scroll = FindVisualDescendant<ScrollViewer>(list);
        return $"viewVisible={view.IsVisible}; images={images.Count}; " +
               $"scroll={scroll?.ViewportWidth}x{scroll?.ViewportHeight}; " +
               string.Join(
                   "; ",
                   images.Take(4).Select(image =>
                       $"loaded={image.IsLoaded}, visible={image.IsVisible}, " +
                       $"requested={image.Width}x{image.Height}, " +
                       $"actual={image.ActualWidth}x{image.ActualHeight}, " +
                       $"data={image.DataContext?.GetType().Name}"));
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

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject =>
        FindVisualDescendants<T>(root).FirstOrDefault();

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

    private sealed class ImmediateThumbnailCache(BitmapSource image)
        : IClipboardThumbnailCache
    {
        public int LoadCount { get; private set; }

        public Task<ClipboardThumbnailPresentation> LoadAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(new ClipboardThumbnailPresentation(
                assetHash,
                ClipboardAssetState.Ready,
                expectedRevision,
                image));
        }
    }

    private sealed class DelayedThumbnailCache(BitmapSource image)
        : IClipboardThumbnailCache
    {
        public List<Request> Requests { get; } = [];

        public Task<ClipboardThumbnailPresentation> LoadAsync(
            string assetHash,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            var request = new Request(
                assetHash,
                expectedRevision,
                image,
                cancellationToken);
            Requests.Add(request);
            return request.Task;
        }

        public void CancelAll()
        {
            foreach (var request in Requests)
            {
                request.Cancel();
            }
        }
    }

    private sealed class Request(
        string assetHash,
        long revision,
        BitmapSource image,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<ClipboardThumbnailPresentation> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Task<ClipboardThumbnailPresentation> Task => _completion.Task;

        public void Complete() => _completion.TrySetResult(
            new ClipboardThumbnailPresentation(
                assetHash,
                ClipboardAssetState.Ready,
                revision,
                image));

        public void Cancel() => _completion.TrySetCanceled();
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
