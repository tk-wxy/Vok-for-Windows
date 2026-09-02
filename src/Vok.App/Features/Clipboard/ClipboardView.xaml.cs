using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

public partial class ClipboardView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private readonly HashSet<Image> _thumbnailImages = [];
    private readonly Dictionary<Image, ThumbnailBinding> _thumbnailBindings = [];
    private bool _thumbnailRefreshPending;

    public ClipboardView()
    {
        InitializeComponent();
        ClipboardItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));
        ClipboardItemsList.AddHandler(
            VirtualizingStackPanel.CleanUpVirtualizedItemEvent,
            new CleanUpVirtualizedItemEventHandler(OnCleanUpVirtualizedItem));
        IsVisibleChanged += OnViewIsVisibleChanged;
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardItemViewModel item } ||
            DataContext is not ClipboardViewModel viewModel)
        {
            return;
        }

        await viewModel.RequestRemoveAsync(item);
    }

    private async void OnItemsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ClipboardViewModel viewModel ||
            ClipboardItemsList.SelectedItem is not ClipboardItemViewModel item)
        {
            return;
        }

        if (e.Key is Key.Enter && viewModel.PasteCommand.CanExecute(item))
        {
            e.Handled = true;
            await viewModel.PasteCommand.ExecuteAsync(item);
        }
        else if (e.Key is Key.Delete)
        {
            e.Handled = true;
            await viewModel.RequestRemoveAsync(item);
        }
    }

    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScheduleThumbnailRefresh();
        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(ClipboardItemsList);
        if (_scrollViewer is null ||
            DataContext is not ClipboardViewModel viewModel ||
            !viewModel.HasMore ||
            viewModel.IsLoading ||
            _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset > 160)
        {
            return;
        }

        await viewModel.LoadMoreAsync();
    }

    private void OnThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        _thumbnailImages.Add(image);
        ScheduleThumbnailRefresh();
    }

    private void OnThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        _thumbnailImages.Remove(image);
        CancelThumbnailBinding(image, resetState: true);
    }

    private void OnThumbnailDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Image image)
        {
            return;
        }

        CancelThumbnailBinding(image, resetState: true);
        ScheduleThumbnailRefresh();
    }

    private void OnCleanUpVirtualizedItem(
        object sender,
        CleanUpVirtualizedItemEventArgs e)
    {
        if (FindNamedVisualDescendant<Image>(e.UIElement, "ThumbnailImage") is { } image)
        {
            CancelThumbnailBinding(image, resetState: true);
        }
    }

    private void OnViewIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScheduleThumbnailRefresh();
        }
        else
        {
            if (DataContext is ClipboardViewModel viewModel)
            {
                viewModel.CancelPendingConfirmations();
            }

            CancelAllThumbnailBindings();
        }
    }

    private void ScheduleThumbnailRefresh()
    {
        if (_thumbnailRefreshPending)
        {
            return;
        }

        _thumbnailRefreshPending = true;
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () =>
            {
                _thumbnailRefreshPending = false;
                RefreshThumbnailBindings();
            });
    }

    private void RefreshThumbnailBindings()
    {
        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(ClipboardItemsList);
        foreach (var image in _thumbnailImages.ToArray())
        {
            if (!IsThumbnailInViewport(image))
            {
                CancelThumbnailBinding(image, resetState: true);
                continue;
            }

            EnsureThumbnailBinding(image);
        }
    }

    private bool IsThumbnailInViewport(Image image)
    {
        var presentationSlot = image.Parent as FrameworkElement ?? image;
        if (!IsVisible || !image.IsLoaded || !image.IsVisible ||
            _scrollViewer is null ||
            _scrollViewer.ViewportWidth <= 0 || _scrollViewer.ViewportHeight <= 0 ||
            presentationSlot.ActualWidth <= 0 || presentationSlot.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = presentationSlot
                .TransformToAncestor(_scrollViewer)
                .TransformBounds(new Rect(presentationSlot.RenderSize));
            return bounds.IntersectsWith(
                new Rect(
                    0,
                    0,
                    _scrollViewer.ViewportWidth,
                    _scrollViewer.ViewportHeight));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void EnsureThumbnailBinding(Image image)
    {
        if (DataContext is not ClipboardViewModel viewModel ||
            image.DataContext is not ClipboardItemViewModel { IsImage: true } item)
        {
            CancelThumbnailBinding(image, resetState: true);
            return;
        }

        var thumbnail = item.Entry.Image?.ThumbnailPng;
        if (thumbnail is null || thumbnail.State is not ClipboardAssetState.Ready)
        {
            CancelThumbnailBinding(image, resetState: false);
            image.Source = null;
            item.SetThumbnailState(ClipboardThumbnailDisplayState.Unavailable);
            return;
        }

        var key = new ThumbnailBindingKey(
            item.Id,
            item.Entry.Revision,
            thumbnail.Hash,
            thumbnail.Revision);
        if (_thumbnailBindings.TryGetValue(image, out var current) &&
            current.Key == key &&
            ReferenceEquals(current.Item, item) &&
            ReferenceEquals(current.ViewModel, viewModel))
        {
            return;
        }

        CancelThumbnailBinding(image, resetState: false);
        var binding = new ThumbnailBinding(viewModel, item, key);
        var operation = viewModel.LoadThumbnailAsync(item, binding.CancellationToken);
        if (operation is null)
        {
            binding.CompleteOperation();
            image.Source = null;
            item.SetThumbnailState(ClipboardThumbnailDisplayState.Unavailable);
            return;
        }

        _thumbnailBindings.Add(image, binding);
        item.PropertyChanged += OnBoundItemPropertyChanged;
        item.SetThumbnailState(ClipboardThumbnailDisplayState.Loading);
        _ = CompleteThumbnailBindingAsync(image, binding, operation);
    }

    private async Task CompleteThumbnailBindingAsync(
        Image image,
        ThumbnailBinding binding,
        Task<ClipboardThumbnailPresentation> operation)
    {
        try
        {
            var presentation = await operation.ConfigureAwait(true);
            if (!IsCurrentThumbnailBinding(image, binding))
            {
                return;
            }

            if (presentation.IsReady &&
                string.Equals(
                    presentation.AssetHash,
                    binding.Key.AssetHash,
                    StringComparison.Ordinal) &&
                presentation.Revision == binding.Key.AssetRevision)
            {
                image.Source = presentation.Image;
                binding.Item.SetThumbnailState(ClipboardThumbnailDisplayState.Ready);
            }
            else
            {
                EndThumbnailBindingAsUnavailable(image, binding);
            }
        }
        catch (OperationCanceledException) when (binding.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentThumbnailBinding(image, binding))
            {
                CancelThumbnailBinding(image, resetState: true);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Clipboard thumbnail presentation failed with {0}.",
                exception.GetType().Name);
            if (IsCurrentThumbnailBinding(image, binding))
            {
                EndThumbnailBindingAsUnavailable(image, binding);
            }
        }
        finally
        {
            binding.CompleteOperation();
        }
    }

    private bool IsCurrentThumbnailBinding(Image image, ThumbnailBinding binding)
    {
        if (!_thumbnailBindings.TryGetValue(image, out var current) ||
            !ReferenceEquals(current, binding) ||
            !ReferenceEquals(DataContext, binding.ViewModel) ||
            !ReferenceEquals(image.DataContext, binding.Item) ||
            !IsThumbnailInViewport(image))
        {
            return false;
        }

        var thumbnail = binding.Item.Entry.Image?.ThumbnailPng;
        return binding.Item.Id == binding.Key.EntryId &&
               binding.Item.Entry.Revision == binding.Key.EntryRevision &&
               thumbnail is not null &&
               string.Equals(
                   thumbnail.Hash,
                   binding.Key.AssetHash,
                   StringComparison.Ordinal) &&
               thumbnail.Revision == binding.Key.AssetRevision &&
               thumbnail.State is ClipboardAssetState.Ready;
    }

    private void OnBoundItemPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClipboardItemViewModel.Entry) ||
            sender is not ClipboardItemViewModel item)
        {
            return;
        }

        foreach (var pair in _thumbnailBindings
                     .Where(pair => ReferenceEquals(pair.Value.Item, item))
                     .ToArray())
        {
            CancelThumbnailBinding(pair.Key, resetState: false);
        }

        ScheduleThumbnailRefresh();
    }

    private void EndThumbnailBindingAsUnavailable(
        Image image,
        ThumbnailBinding binding)
    {
        if (!_thumbnailBindings.TryGetValue(image, out var current) ||
            !ReferenceEquals(current, binding))
        {
            return;
        }

        _thumbnailBindings.Remove(image);
        binding.Item.PropertyChanged -= OnBoundItemPropertyChanged;
        image.Source = null;
        binding.Item.SetThumbnailState(ClipboardThumbnailDisplayState.Unavailable);
    }

    private void CancelThumbnailBinding(Image image, bool resetState)
    {
        if (_thumbnailBindings.Remove(image, out var binding))
        {
            binding.Item.PropertyChanged -= OnBoundItemPropertyChanged;
            binding.CancelOperation();
            image.Source = null;
            if (resetState)
            {
                binding.Item.SetThumbnailState(
                    ClipboardThumbnailDisplayState.NotRequested);
            }
        }
        else
        {
            image.Source = null;
            if (resetState &&
                image.DataContext is ClipboardItemViewModel { IsImage: true } item)
            {
                item.SetThumbnailState(ClipboardThumbnailDisplayState.NotRequested);
            }
        }
    }

    private void CancelAllThumbnailBindings()
    {
        foreach (var image in _thumbnailImages.ToArray())
        {
            CancelThumbnailBinding(image, resetState: true);
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindNamedVisualDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            if (FindNamedVisualDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private readonly record struct ThumbnailBindingKey(
        Guid EntryId,
        long EntryRevision,
        string AssetHash,
        long AssetRevision);

    private sealed class ThumbnailBinding(
        ClipboardViewModel viewModel,
        ClipboardItemViewModel item,
        ThumbnailBindingKey key)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _operationCompleted;

        public ClipboardViewModel ViewModel { get; } = viewModel;

        public ClipboardItemViewModel Item { get; } = item;

        public ThumbnailBindingKey Key { get; } = key;

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void CancelOperation()
        {
            if (Volatile.Read(ref _operationCompleted) == 0)
            {
                _cancellation.Cancel();
            }
        }

        public void CompleteOperation()
        {
            if (Interlocked.Exchange(ref _operationCompleted, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }
    }
}
