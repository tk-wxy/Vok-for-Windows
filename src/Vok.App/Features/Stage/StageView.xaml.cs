using System.Collections.ObjectModel;
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
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth),
        typeof(double),
        typeof(StageView),
        new PropertyMetadata(StageCardLayoutCalculator.MinimumCardWidth));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight),
        typeof(double),
        typeof(StageView),
        new PropertyMetadata(StageCardLayoutCalculator.CardHeight));

    public static readonly DependencyProperty CardRowUnitHeightProperty =
        DependencyProperty.Register(
            nameof(CardRowUnitHeight),
            typeof(double),
            typeof(StageView),
            new PropertyMetadata(
                StageCardLayoutCalculator.CardHeight +
                StageCardLayoutCalculator.VerticalGap));

    public static readonly DependencyProperty CardRowSurfaceWidthProperty =
        DependencyProperty.Register(
            nameof(CardRowSurfaceWidth),
            typeof(double),
            typeof(StageView),
            new PropertyMetadata(StageCardLayoutCalculator.MinimumCardWidth));

    private readonly StageCardRowsProjection _cardRowsProjection;
    private ObservableCollection<StageItemViewModel>? _observedItems;
    private StageViewModel? _observedViewModel;
    private ScrollViewer? _scrollViewer;
    private readonly HashSet<Border> _availabilityCards = [];
    private readonly Dictionary<Border, AvailabilityBinding> _availabilityBindings = [];
    private StageCardLayoutProfile? _cardLayoutProfile;
    private ScrollAnchor? _pendingScrollAnchor;
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private bool _scrollResetPending;
    private bool _cardLayoutRefreshPending;
    private bool _rowPublicationRefreshPending;
    private bool _availabilityRefreshPending;

    public StageView()
    {
        _cardRowsProjection = new StageCardRowsProjection(Dispatcher);
        _cardRowsProjection.RowsPublished += OnCardRowsPublished;
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        StageItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnStageItemsScrollChanged));
        StageItemsList.AddHandler(
            VirtualizingStackPanel.CleanUpVirtualizedItemEvent,
            new CleanUpVirtualizedItemEventHandler(OnCleanUpVirtualizedItem));
        IsVisibleChanged += OnViewIsVisibleChanged;
    }

    public ObservableCollection<StageCardRowViewModel> CardRows =>
        _cardRowsProjection.Rows;

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetCurrentValue(CardWidthProperty, value);
    }

    public double CardHeight
    {
        get => (double)GetValue(CardHeightProperty);
        private set => SetCurrentValue(CardHeightProperty, value);
    }

    public double CardRowUnitHeight
    {
        get => (double)GetValue(CardRowUnitHeightProperty);
        private set => SetCurrentValue(CardRowUnitHeightProperty, value);
    }

    public double CardRowSurfaceWidth
    {
        get => (double)GetValue(CardRowSurfaceWidthProperty);
        private set => SetCurrentValue(CardRowSurfaceWidthProperty, value);
    }

    internal StageCardLayoutProfile? CardLayoutProfile => _cardLayoutProfile;

    internal double CardViewportWidth => GetStageScrollViewer()?.ViewportWidth ?? 0;

    internal int ActiveAvailabilityBindingCount => _availabilityBindings.Count;

    internal int TrackedAvailabilityCardCount => _availabilityCards.Count;

    internal bool HasPendingDeferredWork =>
        _cardLayoutRefreshPending ||
        _rowPublicationRefreshPending ||
        _availabilityRefreshPending ||
        _cardRowsProjection.HasPendingRebuild;

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
        DetachObservedViewModel();
        if (IsLoaded && e.NewValue is StageViewModel newViewModel)
        {
            AttachObservedViewModel(newViewModel);
        }

        ScheduleCardLayoutRefresh();
        ScheduleAvailabilityRefresh();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCardLayoutFromRealizedViewport();
        if (DataContext is StageViewModel viewModel)
        {
            AttachObservedViewModel(viewModel);
        }

        ScheduleCardLayoutRefresh();
        ScheduleAvailabilityRefresh();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        DetachObservedViewModel();
        CancelAllAvailabilityBindings();
        _availabilityCards.Clear();
        _pendingScrollAnchor = null;
        _scrollResetPending = false;
        _scrollViewer = null;
    }

    private void AttachObservedViewModel(StageViewModel viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        _observedViewModel = viewModel;
        _observedItems = viewModel.Items;
        _observedItems.CollectionChanged += OnItemsCollectionChanged;
        viewModel.ItemAuthorityChanged += OnItemAuthorityChanged;
        _scrollResetPending = true;
        _pendingScrollAnchor = null;
        _cardRowsProjection.Attach(_observedItems);
    }

    private void DetachObservedViewModel()
    {
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged -= OnItemsCollectionChanged;
            _observedItems = null;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.ItemAuthorityChanged -= OnItemAuthorityChanged;
            _observedViewModel = null;
        }

        CancelAllAvailabilityBindings();
        _cardRowsProjection.Attach(null);
    }

    private void OnItemAuthorityChanged(StageItemViewModel item)
    {
        foreach (var card in _availabilityCards.Where(
                     card => ReferenceEquals(card.DataContext, item)).ToArray())
        {
            CancelAvailabilityBinding(card, resetState: false);
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
        _pendingScrollAnchor = null;
    }

    private void OnCardRowsPublished()
    {
        if (_rowPublicationRefreshPending)
        {
            return;
        }

        _rowPublicationRefreshPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _rowPublicationRefreshPending = false;
            if (!IsLoaded)
            {
                return;
            }

            var scrollViewer = GetStageScrollViewer();
            if (_scrollResetPending)
            {
                _scrollResetPending = false;
                _pendingScrollAnchor = null;
                StageItemsList.UpdateLayout();
                scrollViewer?.ScrollToTop();
            }
            else if (_pendingScrollAnchor is { } anchor)
            {
                _pendingScrollAnchor = null;
                RestoreScrollAnchor(anchor, scrollViewer);
            }

            ScheduleAvailabilityRefresh();
        });
    }

    private void OnStageItemsViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleCardLayoutRefresh();

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

    private void OnStageItemsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) > 0.001)
        {
            ScheduleCardLayoutRefresh();
        }

        ScheduleAvailabilityRefresh();
    }

    private void OnCleanUpVirtualizedItem(
        object sender,
        CleanUpVirtualizedItemEventArgs e) =>
        CancelAvailabilityBindingsForRow(e.UIElement);

    internal void CancelAvailabilityBindingsForRow(DependencyObject rowRoot)
    {
        ArgumentNullException.ThrowIfNull(rowRoot);
        foreach (var card in FindNamedVisualDescendants<Border>(
                     rowRoot,
                     "StageItemCard").ToArray())
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

    private void ScheduleCardLayoutRefresh()
    {
        if (_cardLayoutRefreshPending)
        {
            return;
        }

        _cardLayoutRefreshPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _cardLayoutRefreshPending = false;
            if (IsLoaded)
            {
                ApplyCardLayoutFromRealizedViewport();
            }
        });
    }

    private void ApplyCardLayoutFromRealizedViewport()
    {
        var viewportWidth = GetStageScrollViewer()?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        var profile = StageCardLayoutCalculator.Calculate(viewportWidth);
        if (profile == _cardLayoutProfile)
        {
            return;
        }

        var columnsChanged = profile.ColumnCount != _cardLayoutProfile?.ColumnCount;
        if (columnsChanged)
        {
            _pendingScrollAnchor = CaptureScrollAnchor();
            CancelAllAvailabilityBindings();
        }

        _cardLayoutProfile = profile;
        CardWidth = profile.CardWidth;
        CardHeight = profile.CardHeight;
        CardRowUnitHeight = profile.RowUnitHeight;
        CardRowSurfaceWidth = profile.ColumnCount == 1
            ? profile.CardWidth
            : profile.ColumnCount * (profile.CardWidth + profile.HorizontalGap);

        if (columnsChanged)
        {
            _cardRowsProjection.SetColumnCount(profile.ColumnCount);
        }

        ScheduleAvailabilityRefresh();
    }

    private ScrollAnchor? CaptureScrollAnchor()
    {
        var scrollViewer = GetStageScrollViewer();
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
        {
            return null;
        }

        return _availabilityCards
            .Select(card => TryGetVisibleCardPosition(card, scrollViewer))
            .Where(static position => position is not null)
            .Select(static position => position!.Value)
            .OrderBy(static position => position.Top)
            .ThenBy(static position => position.Left)
            .Select(static position => (ScrollAnchor?)new ScrollAnchor(
                position.ItemId,
                position.Top))
            .FirstOrDefault();
    }

    private static VisibleCardPosition? TryGetVisibleCardPosition(
        Border card,
        ScrollViewer scrollViewer)
    {
        if (card.DataContext is not StageItemViewModel item ||
            !card.IsLoaded || card.ActualWidth <= 0 || card.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            var bounds = card
                .TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(card.RenderSize));
            return bounds.Bottom > 0 && bounds.Top < scrollViewer.ViewportHeight
                ? new VisibleCardPosition(item.Id, bounds.Top, bounds.Left)
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void RestoreScrollAnchor(ScrollAnchor anchor, ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null)
        {
            return;
        }

        var row = CardRows.FirstOrDefault(row =>
            row.Items.Any(item => item.Id == anchor.ItemId));
        if (row is null)
        {
            return;
        }

        StageItemsList.ScrollIntoView(row);
        StageItemsList.UpdateLayout();
        var card = FindNamedVisualDescendants<Border>(StageItemsList, "StageItemCard")
            .FirstOrDefault(candidate =>
                candidate.DataContext is StageItemViewModel item &&
                item.Id == anchor.ItemId);
        if (card is null)
        {
            return;
        }

        try
        {
            var bounds = card
                .TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(card.RenderSize));
            scrollViewer.ScrollToVerticalOffset(
                Math.Max(0, scrollViewer.VerticalOffset + bounds.Top - anchor.RelativeTop));
        }
        catch (InvalidOperationException)
        {
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

    private ScrollViewer? GetStageScrollViewer() =>
        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(StageItemsList);

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

    private static IEnumerable<T> FindNamedVisualDescendants<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                yield return match;
            }

            foreach (var descendant in FindNamedVisualDescendants<T>(child, name))
            {
                yield return descendant;
            }
        }
    }

    private readonly record struct ScrollAnchor(Guid ItemId, double RelativeTop);

    private readonly record struct VisibleCardPosition(
        Guid ItemId,
        double Top,
        double Left);

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
