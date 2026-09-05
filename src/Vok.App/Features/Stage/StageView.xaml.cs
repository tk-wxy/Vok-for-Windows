using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public partial class StageView : UserControl
{
    private INotifyCollectionChanged? _observedItems;
    private ScrollViewer? _scrollViewer;
    private readonly HashSet<Border> _availabilityCards = [];
    private readonly Dictionary<Border, AvailabilityBinding> _availabilityBindings = [];
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private bool _scrollResetPending;
    private bool _availabilityRefreshPending;

    public StageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        StageItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnStageItemsScrollChanged));
        StageItemsList.AddHandler(
            VirtualizingStackPanel.CleanUpVirtualizedItemEvent,
            new CleanUpVirtualizedItemEventHandler(OnCleanUpVirtualizedItem));
        IsVisibleChanged += OnViewIsVisibleChanged;
    }

    internal void SetExternalDragState(bool sessionActive, bool targeted)
    {
        if (_externalDragActive == sessionActive &&
            _externalDragTargeted == targeted)
        {
            return;
        }

        _externalDragActive = sessionActive;
        _externalDragTargeted = targeted;
        DropOverlay.Visibility = sessionActive && targeted
            ? Visibility.Visible
            : Visibility.Collapsed;
        StageInteractionSurface.IsHitTestVisible = !sessionActive;
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged -= OnItemsCollectionChanged;
        }

        CancelAllAvailabilityBindings();

        _observedItems = (e.NewValue as StageViewModel)?.Items;
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged += OnItemsCollectionChanged;
        }

        ScheduleAvailabilityRefresh();
    }

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not NotifyCollectionChangedAction.Reset || _scrollResetPending)
        {
            return;
        }

        _scrollResetPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollResetPending = false;
            FindVisualDescendant<ScrollViewer>(StageItemsList)?.ScrollToTop();
            ScheduleAvailabilityRefresh();
        });
    }

    private void OnStageItemCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        _availabilityCards.Add(card);
        ScheduleAvailabilityRefresh();
    }

    private void OnStageItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnStageItemCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        _availabilityCards.Remove(card);
        CancelAvailabilityBinding(card, resetState: true);
    }

    private void OnStageItemCardDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        CancelAvailabilityBinding(card, resetState: true);
        ScheduleAvailabilityRefresh();
    }

    private void OnStageItemsScrollChanged(object sender, ScrollChangedEventArgs e) =>
        ScheduleAvailabilityRefresh();

    private void OnCleanUpVirtualizedItem(
        object sender,
        CleanUpVirtualizedItemEventArgs e)
    {
        if (FindNamedVisualDescendant<Border>(e.UIElement, "StageItemCard") is { } card)
        {
            CancelAvailabilityBinding(card, resetState: true);
        }
    }

    private void OnViewIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScheduleAvailabilityRefresh();
        }
        else
        {
            CancelAllAvailabilityBindings();
        }
    }

    private void ScheduleAvailabilityRefresh()
    {
        if (_availabilityRefreshPending)
        {
            return;
        }

        _availabilityRefreshPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _availabilityRefreshPending = false;
            RefreshAvailabilityBindings();
        });
    }

    private void RefreshAvailabilityBindings()
    {
        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(StageItemsList);
        foreach (var card in _availabilityCards.ToArray())
        {
            if (!IsCardInViewport(card))
            {
                CancelAvailabilityBinding(card, resetState: true);
                continue;
            }

            EnsureAvailabilityBinding(card);
        }
    }

    private bool IsCardInViewport(Border card)
    {
        if (!IsVisible || !card.IsLoaded || !card.IsVisible ||
            _scrollViewer is null ||
            _scrollViewer.ViewportWidth <= 0 || _scrollViewer.ViewportHeight <= 0 ||
            card.ActualWidth <= 0 || card.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = card
                .TransformToAncestor(_scrollViewer)
                .TransformBounds(new Rect(card.RenderSize));
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

    private void EnsureAvailabilityBinding(Border card)
    {
        if (DataContext is not StageViewModel viewModel ||
            card.DataContext is not StageItemViewModel item)
        {
            CancelAvailabilityBinding(card, resetState: true);
            return;
        }

        var key = new AvailabilityBindingKey(item.Id, item.Revision);
        if (_availabilityBindings.TryGetValue(card, out var current) &&
            current.Key == key &&
            ReferenceEquals(current.Item, item) &&
            ReferenceEquals(current.ViewModel, viewModel))
        {
            return;
        }

        CancelAvailabilityBinding(card, resetState: false);
        var binding = new AvailabilityBinding(viewModel, item, key);
        _availabilityBindings.Add(card, binding);
        _ = CompleteAvailabilityBindingAsync(
            card,
            binding,
            viewModel.InspectAvailabilityAsync(item, binding.CancellationToken));
    }

    private async Task CompleteAvailabilityBindingAsync(
        Border card,
        AvailabilityBinding binding,
        Task<StageFileAvailability> operation)
    {
        try
        {
            var availability = await operation.ConfigureAwait(true);
            if (IsCurrentAvailabilityBinding(card, binding))
            {
                _ = binding.Item.ApplyAvailability(
                    binding.Key.Revision,
                    availability);
            }
        }
        catch (OperationCanceledException) when (binding.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAvailabilityBinding(card, binding))
            {
                _ = binding.Item.ApplyAvailability(
                    binding.Key.Revision,
                    StageFileAvailability.Unavailable);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentAvailabilityBinding(card, binding))
            {
                _ = binding.Item.ApplyAvailability(
                    binding.Key.Revision,
                    StageFileAvailability.Unavailable);
            }

            Trace.TraceWarning(
                "Stage file availability inspection failed with {0}.",
                exception.GetType().Name);
        }
        finally
        {
            binding.CompleteOperation();
        }
    }

    private bool IsCurrentAvailabilityBinding(
        Border card,
        AvailabilityBinding binding) =>
        _availabilityBindings.TryGetValue(card, out var current) &&
        ReferenceEquals(current, binding) &&
        ReferenceEquals(card.DataContext, binding.Item) &&
        ReferenceEquals(DataContext, binding.ViewModel) &&
        binding.Item.Id == binding.Key.ItemId &&
        binding.Item.Revision == binding.Key.Revision;

    private void CancelAvailabilityBinding(Border card, bool resetState)
    {
        if (!_availabilityBindings.Remove(card, out var binding))
        {
            return;
        }

        binding.CancelOperation();
        if (resetState)
        {
            binding.Item.ResetAvailability(binding.Key.Revision);
        }
    }

    private void CancelAllAvailabilityBindings()
    {
        foreach (var card in _availabilityBindings.Keys.ToArray())
        {
            CancelAvailabilityBinding(card, resetState: true);
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

    private readonly record struct AvailabilityBindingKey(
        Guid ItemId,
        long Revision);

    private sealed class AvailabilityBinding(
        StageViewModel viewModel,
        StageItemViewModel item,
        AvailabilityBindingKey key)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _operationCompleted;

        public StageViewModel ViewModel { get; } = viewModel;

        public StageItemViewModel Item { get; } = item;

        public AvailabilityBindingKey Key { get; } = key;

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
