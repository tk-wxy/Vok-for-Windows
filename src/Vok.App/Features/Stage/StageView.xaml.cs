using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public partial class StageView : UserControl
{
    private const double DragEdgeZone = 36;
    private const double MaximumAutoScrollSpeed = 720;
    private const int MaximumPendingAvailabilityInspections = 4;
    private const double EdgePrefetchViewportFraction = 0.25;
    private const int EdgePrefetchMinimumRowCount = 2;
    private static readonly Duration ReorderShiftDuration =
        new(TimeSpan.FromMilliseconds(140));
    private static readonly CubicEase ReorderShiftEasing = CreateReorderShiftEasing();

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth),
        typeof(double),
        typeof(StageView),
        new PropertyMetadata(StageCardLayoutCalculator.MinimumCardWidth));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight),
        typeof(double),
        typeof(StageView),
        new PropertyMetadata(StageCardLayoutCalculator.DefaultCardHeight));

    public static readonly DependencyProperty CardRowUnitHeightProperty =
        DependencyProperty.Register(
            nameof(CardRowUnitHeight),
            typeof(double),
            typeof(StageView),
            new PropertyMetadata(
                StageCardLayoutCalculator.DefaultRowUnitHeight,
                OnCardRowUnitHeightChanged));

    public static readonly DependencyProperty CardRowSurfaceWidthProperty =
        DependencyProperty.Register(
            nameof(CardRowSurfaceWidth),
            typeof(double),
            typeof(StageView),
            new PropertyMetadata(StageCardLayoutCalculator.MinimumCardWidth));

    public static readonly DependencyProperty IsContextMenuOpenProperty =
        DependencyProperty.RegisterAttached(
            "IsContextMenuOpen",
            typeof(bool),
            typeof(StageView),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsInteractionActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsInteractionActive",
            typeof(bool),
            typeof(StageView),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsKeyboardContextActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsKeyboardContextActive",
            typeof(bool),
            typeof(StageView),
            new PropertyMetadata(false));

    private readonly StageCardRowsProjection _cardRowsProjection;
    private ObservableCollection<StageItemViewModel>? _observedItems;
    private StageViewModel? _observedViewModel;
    private ScrollViewer? _scrollViewer;
    private readonly HashSet<Border> _availabilityCards = [];
    private readonly Dictionary<Border, AvailabilityBinding> _availabilityBindings = [];
    private readonly Dictionary<StageItemViewModel, long>
        _availabilityValidatedGenerations = [];
    private StageCardLayoutProfile? _cardLayoutProfile;
    private ScrollAnchor? _pendingScrollAnchor;
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private bool _scrollResetPending;
    private bool _cardLayoutRefreshPending;
    private bool _rowPublicationRefreshPending;
    private bool _availabilityRefreshPending;
    private bool _availabilityRequestsAllowed = true;
    private DispatcherOperation? _pendingEdgePrefetchEvaluation;
    private CancellationTokenSource? _edgePrefetchCancellation;
    private StagePageDirection? _edgePrefetchIntent;
    private bool _edgePrefetchActive;
    private bool _edgePrefetchReevaluateAfterCompletion;
    private bool _suppressEdgePrefetchScrollChanged;
    private long _availabilityRefreshGeneration;
    private int _availabilityOutstandingOperations;
    private Border? _pressedCard;
    private StageItemViewModel? _pressedItem;
    private Point _pressPoint;
    private Point _dragPointerOffset;
    private Point _lastPointerInViewport;
    private long _reorderGeneration;
    private int _lastPreviewIndex = -1;
    private IReadOnlyList<StageReorderCardBounds> _reorderCardBounds = [];
    private readonly Dictionary<Border, ReorderTranslationTarget>
        _reorderTranslationTargets = [];
    private bool _reorderVisualRefreshPending;
    private bool _autoScrollAttached;
    private double _autoScrollVelocity;
    private long _lastAutoScrollTimestamp;

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
        AddHandler(
            Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPreviewMouseMove),
            handledEventsToo: true);
        AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp),
            handledEventsToo: true);
        AddHandler(
            Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
        LostMouseCapture += OnLostMouseCapture;
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
        set => SetCurrentValue(CardHeightProperty, value);
    }

    public double CardRowUnitHeight
    {
        get => (double)GetValue(CardRowUnitHeightProperty);
        set => SetCurrentValue(CardRowUnitHeightProperty, value);
    }

    public double CardRowSurfaceWidth
    {
        get => (double)GetValue(CardRowSurfaceWidthProperty);
        private set => SetCurrentValue(CardRowSurfaceWidthProperty, value);
    }

    public static bool GetIsContextMenuOpen(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsContextMenuOpenProperty);
    }

    public static void SetIsContextMenuOpen(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsContextMenuOpenProperty, value);
    }

    public static bool GetIsInteractionActive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsInteractionActiveProperty);
    }

    public static void SetIsInteractionActive(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsInteractionActiveProperty, value);
    }

    public static bool GetIsKeyboardContextActive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsKeyboardContextActiveProperty);
    }

    public static void SetIsKeyboardContextActive(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsKeyboardContextActiveProperty, value);
    }

    private static void OnCardRowUnitHeightChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is StageView view && view.IsInitialized)
        {
            view.ScheduleCardLayoutRefresh();
        }
    }

    internal event Action? ViewportMutationSettled;

    internal event Action? RowsProjectionPublished;

    internal event Action<StagePageDirection>? EdgePrefetchStarted;

    internal event Action<StagePageDirection, bool>? EdgePrefetchCompleted;

    internal StageCardLayoutProfile? CardLayoutProfile => _cardLayoutProfile;

    internal double CardViewportWidth => GetStageScrollViewer()?.ViewportWidth ?? 0;

    internal int ActiveAvailabilityBindingCount => _availabilityBindings.Count;

    internal int OutstandingAvailabilityOperationCount =>
        _availabilityOutstandingOperations;

    internal int TrackedAvailabilityCardCount => _availabilityCards.Count;

    internal bool HasPendingDeferredWork =>
        _cardLayoutRefreshPending ||
        _rowPublicationRefreshPending ||
        _availabilityRefreshPending ||
        _reorderVisualRefreshPending ||
        _pendingEdgePrefetchEvaluation is { Status: DispatcherOperationStatus.Pending } ||
        _cardRowsProjection.HasPendingRebuild;

    internal bool HasPendingViewportMutationWork =>
        _cardLayoutRefreshPending ||
        _rowPublicationRefreshPending ||
        _reorderVisualRefreshPending ||
        _pendingEdgePrefetchEvaluation is { Status: DispatcherOperationStatus.Pending } ||
        _cardRowsProjection.HasPendingRebuild;

    internal bool HasActiveReorderPointer => _pressedItem is not null;

    internal bool HasAutoScrollSubscription => _autoScrollAttached;

    internal bool HasActiveEdgePrefetch => _edgePrefetchActive;

    internal void SetExternalDragState(bool sessionActive, bool targeted)
    {
        if (sessionActive)
        {
            CancelEdgePrefetch();
            CancelAllInteractions();
        }

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
        foreach (var card in _availabilityCards.ToArray())
        {
            if (sessionActive)
            {
                ResetContextMenuState(card);
            }
            else
            {
                UpdateCardInteractionState(card);
            }
        }

        if (!sessionActive)
        {
            ScheduleAvailabilityRefresh();
            ScheduleEdgePrefetch();
        }
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        CancelAllInteractions(_observedViewModel);
        DetachObservedViewModel();
        if (e.NewValue is StageViewModel newViewModel)
        {
            AttachObservedViewModel(newViewModel);
        }

        ScheduleCardLayoutRefresh();
        ScheduleAvailabilityRefresh();
        ScheduleEdgePrefetch();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        TraceStage("StageLoaded");
        AttachRevealLayoutObservation();
        if (_activeRevealGeneration == 0 && _completedRevealGeneration == 0)
        {
            _availabilityRequestsAllowed = true;
        }
        ApplyCardLayoutFromRealizedViewport();
        if (DataContext is StageViewModel viewModel)
        {
            AttachObservedViewModel(viewModel);
        }

        ScheduleCardLayoutRefresh();
        ScheduleAvailabilityRefresh();
        ScheduleEdgePrefetch();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        TraceStage("StageUnloaded");
        CancelRevealCore();
        _completedRevealGeneration = 0;
        _availabilityRequestsAllowed = false;
        DetachRevealDiagnostics();
        CancelAllInteractions();
        CancelEdgePrefetch();
        DetachObservedViewModel();
        CancelAllAvailabilityBindings(resetState: false);
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
        viewModel.WindowProjectionChanging += OnWindowProjectionChanging;
        viewModel.PropertyChanged += OnObservedViewModelPropertyChanged;
        _scrollResetPending = true;
        _pendingScrollAnchor = null;
        _cardRowsProjection.Attach(_observedItems);
        TraceStage("StageSourceAttached");
    }

    private void DetachObservedViewModel()
    {
        var hadSource = _observedItems is not null;
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged -= OnItemsCollectionChanged;
            _observedItems = null;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.ItemAuthorityChanged -= OnItemAuthorityChanged;
            _observedViewModel.WindowProjectionChanging -= OnWindowProjectionChanging;
            _observedViewModel.PropertyChanged -= OnObservedViewModelPropertyChanged;
            _observedViewModel = null;
        }

        CancelEdgePrefetch();
        CancelAllAvailabilityBindings(resetState: false);
        _availabilityValidatedGenerations.Clear();
        _cardRowsProjection.Attach(null);
        if (hadSource) TraceStage("StageSourceDetached");
    }

    private void OnObservedViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _observedViewModel))
        {
            return;
        }

        if (e.PropertyName is nameof(StageViewModel.IsReordering) or
            nameof(StageViewModel.IsCommitting))
        {
            foreach (var card in _availabilityCards.ToArray())
            {
                UpdateCardInteractionState(card);
            }

            if (IsReorderInteractionActive())
            {
                CancelEdgePrefetch();
            }
            else
            {
                ScheduleAvailabilityRefresh();
                ScheduleEdgePrefetch();
            }
        }

        if (e.PropertyName is nameof(StageViewModel.IsLoading) or
            nameof(StageViewModel.IsEmpty) or
            nameof(StageViewModel.ShowErrorState))
        {
            _staticPresentationVersion = unchecked(_staticPresentationVersion + 1);
            EvaluateRevealReadiness();
        }
    }

    private void OnItemAuthorityChanged(StageItemViewModel item)
    {
        _staticPresentationVersion = unchecked(_staticPresentationVersion + 1);
        _availabilityValidatedGenerations.Remove(item);
        foreach (var card in _availabilityCards.Where(
                     card => ReferenceEquals(card.DataContext, item)).ToArray())
        {
            CancelAvailabilityBinding(card, resetState: false);
        }

        ScheduleAvailabilityRefresh();
    }

    private void OnWindowProjectionChanging(StageRollingWindowChange change)
    {
        if (change.IsReset)
        {
            _pendingScrollAnchor = null;
            _scrollResetPending = true;
            return;
        }

        if (!CanPreserveEdgeScrollPosition())
        {
            _pendingScrollAnchor = null;
            return;
        }

        var evictedIds = change.EvictedItems
            .Select(static item => item.Id)
            .ToHashSet();
        _pendingScrollAnchor = CaptureScrollAnchor(evictedIds);
    }

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            _availabilityValidatedGenerations.Clear();
        }
        else if (e.OldItems is not null)
        {
            foreach (var oldItem in e.OldItems.OfType<StageItemViewModel>())
            {
                _availabilityValidatedGenerations.Remove(oldItem);
            }
        }

        if (e.Action is not NotifyCollectionChangedAction.Reset || _scrollResetPending)
        {
            return;
        }

        _scrollResetPending = true;
        _pendingScrollAnchor = null;
    }

    private void OnCardRowsPublished()
    {
        TraceStage("StageRowsPublished");
        RowsProjectionPublished?.Invoke();
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
                _suppressEdgePrefetchScrollChanged = true;
                try
                {
                    RestoreScrollAnchor(anchor, scrollViewer);
                }
                finally
                {
                    _suppressEdgePrefetchScrollChanged = false;
                }
            }

            ViewportMutationSettled?.Invoke();
            ScheduleAvailabilityRefresh();
            ScheduleEdgePrefetch();
            if (_reorderGeneration != 0)
            {
                ScheduleReorderVisualRefresh();
            }
            TraceStage("StageScrollApplied");
            EvaluateRevealReadiness();
            if (_revealTrace.Enabled) OnRevealLayoutUpdated(this, EventArgs.Empty);
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
        UpdateCardInteractionState(card);
        ScheduleAvailabilityRefresh();
    }

    private void OnStageItemCardMouseStateChanged(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
        {
            UpdateCardInteractionState(card);
        }
    }

    private void OnStageItemCardPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Border card ||
            card.DataContext is not StageItemViewModel item ||
            DataContext is not StageViewModel viewModel ||
            e.OriginalSource is not DependencyObject originalSource ||
            !IsCardDragSurface(originalSource, card))
        {
            return;
        }

        if (_externalDragActive || viewModel.IsLoading || viewModel.IsCommitting ||
            viewModel.IsReordering || item.IsBusy || viewModel.Items.Count < 2)
        {
            return;
        }

        e.Handled = true;
        CancelPointerInteraction(viewModel);
        ResetContextMenuState(card);
        _pressedCard = card;
        _pressedItem = item;
        _pressPoint = e.GetPosition(StageItemsViewport);
        _dragPointerOffset = e.GetPosition(card);
        _lastPointerInViewport = _pressPoint;
        _lastPreviewIndex = viewModel.Items.IndexOf(item);
        if (!Mouse.Capture(this, CaptureMode.Element))
        {
            ClearPointerState();
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is null || _pressedItem is null)
        {
            return;
        }

        e.Handled = true;
        if (e.LeftButton is not MouseButtonState.Pressed)
        {
            CancelPointerInteraction();
            return;
        }

        var pointer = e.GetPosition(StageItemsViewport);
        if (_reorderGeneration == 0)
        {
            var delta = pointer - _pressPoint;
            if (!StageReorderGeometry.HasExceededDragThreshold(
                    delta,
                    SystemParameters.MinimumHorizontalDragDistance,
                    SystemParameters.MinimumVerticalDragDistance))
            {
                return;
            }

            if (DataContext is not StageViewModel viewModel)
            {
                CancelPointerInteraction();
                return;
            }

            _reorderGeneration = viewModel.BeginReorder(_pressedItem);
            if (_reorderGeneration == 0)
            {
                CancelPointerInteraction(viewModel);
                return;
            }

            _lastPreviewIndex = viewModel.Items.IndexOf(_pressedItem);
            _reorderCardBounds = GetRealizedReorderCards(viewModel);
            if (_reorderCardBounds.All(card => card.Index != _lastPreviewIndex))
            {
                CancelPointerInteraction(viewModel);
                return;
            }

            ShowInternalReorderGhost(_pressedCard, _pressedItem);
        }

        UpdateInternalReorder(pointer);
    }

    private async void OnPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Left || _pressedItem is null)
        {
            return;
        }

        e.Handled = true;
        var generation = _reorderGeneration;
        var viewModel = DataContext as StageViewModel;
        ClearPointerOwnership();
        ReleasePointerCapture();
        if (generation == 0 || viewModel is null)
        {
            ClearReorderVisuals();
            return;
        }

        try
        {
            var commit = viewModel.CommitReorderAsync(generation);
            ClearReorderVisuals();
            _ = await commit.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The view-model lifetime owns shutdown cancellation.
        }
        catch (ObjectDisposedException)
        {
            // The view-model was disposed while the captured pointer was ending.
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_reorderGeneration == 0)
        {
            if (e.Delta != 0)
            {
                ScheduleEdgePrefetch(
                    e.Delta < 0
                        ? StagePageDirection.TowardOlder
                        : StagePageDirection.TowardNewer);
            }

            return;
        }

        if (GetStageScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        e.Handled = true;
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset - (e.Delta / 3.0));
        ScheduleReorderVisualRefresh();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedItem is not null)
        {
            CancelPointerInteraction();
        }
    }

    internal bool TryCancelReorderInteraction()
    {
        if (_pressedItem is not null)
        {
            CancelPointerInteraction();
            return true;
        }

        if (_observedViewModel is { IsReordering: true, IsCommitting: false } viewModel)
        {
            _ = viewModel.TryCancelReorderPreview();
            return true;
        }

        return false;
    }

    internal void CancelAllInteractions() =>
        CancelAllInteractions(_observedViewModel ?? DataContext as StageViewModel);

    private void CancelAllInteractions(StageViewModel? viewModel)
    {
        CancelPointerInteraction(viewModel);
        _ = viewModel?.TryCancelReorderPreview();
    }

    private void CancelPointerInteraction(StageViewModel? viewModel = null)
    {
        var generation = _reorderGeneration;
        ClearPointerState();
        ReleasePointerCapture();
        if (generation != 0)
        {
            var owner = viewModel ?? _observedViewModel ?? DataContext as StageViewModel;
            _ = owner?.TryCancelReorderPreview(generation);
        }

        ScheduleAvailabilityRefresh();
    }

    private void UpdateInternalReorder(Point pointerInViewport)
    {
        _lastPointerInViewport = pointerInViewport;
        PositionInternalReorderGhost(pointerInViewport);
        UpdateAutoScroll(pointerInViewport);
        PreviewReorderAt(pointerInViewport);
    }

    private void PreviewReorderAt(Point pointerInViewport)
    {
        if (_reorderGeneration == 0 ||
            DataContext is not StageViewModel viewModel || viewModel.Items.Count == 0)
        {
            return;
        }

        var realizedCards = _reorderCardBounds;
        if (realizedCards.Count == 0)
        {
            return;
        }

        var targetIndex = StageReorderGeometry.GetTargetIndex(
            pointerInViewport,
            realizedCards,
            viewModel.Items.Count);
        if (targetIndex == _lastPreviewIndex)
        {
            return;
        }

        if (viewModel.PreviewReorder(_reorderGeneration, targetIndex))
        {
            _lastPreviewIndex = targetIndex;
            AnimateReorderPreview(viewModel, targetIndex);
        }
    }

    private IReadOnlyList<StageReorderCardBounds> GetRealizedReorderCards(
        StageViewModel viewModel)
    {
        var cards = new List<StageReorderCardBounds>();
        foreach (var card in _availabilityCards)
        {
            if (card.DataContext is not StageItemViewModel item || !card.IsLoaded ||
                card.ActualWidth <= 0 || card.ActualHeight <= 0)
            {
                continue;
            }

            var index = viewModel.Items.IndexOf(item);
            if (index < 0)
            {
                continue;
            }

            try
            {
                var bounds = card.TransformToAncestor(StageItemsViewport)
                    .TransformBounds(new Rect(card.RenderSize));
                if (TryGetReorderTranslation(card) is { } translation)
                {
                    bounds.Offset(-translation.X, -translation.Y);
                }

                cards.Add(new StageReorderCardBounds(
                    index,
                    bounds));
            }
            catch (InvalidOperationException)
            {
            }
        }

        return cards
            .GroupBy(static card => card.Index)
            .Select(static group => group.First())
            .OrderBy(static card => card.Index)
            .ToArray();
    }

    private void ShowInternalReorderGhost(
        Border card,
        StageItemViewModel item)
    {
        InternalReorderGhost.DataContext = item;
        InternalReorderGhost.Width = Math.Max(1, card.ActualWidth);
        InternalReorderGhost.Height = Math.Max(1, card.ActualHeight);
        InternalReorderGhost.Visibility = Visibility.Visible;
        PositionInternalReorderGhost(_lastPointerInViewport);
    }

    private void PositionInternalReorderGhost(Point pointer)
    {
        if (InternalReorderGhost.Visibility is not Visibility.Visible)
        {
            return;
        }

        if (InternalReorderGhost.RenderTransform is not TranslateTransform translation)
        {
            return;
        }

        translation.X = Math.Clamp(
            pointer.X - _dragPointerOffset.X,
            0,
            Math.Max(0, StageItemsViewport.ActualWidth - InternalReorderGhost.Width));
        translation.Y = Math.Clamp(
            pointer.Y - _dragPointerOffset.Y,
            0,
            Math.Max(0, StageItemsViewport.ActualHeight - InternalReorderGhost.Height));
    }

    private void AnimateReorderPreview(StageViewModel viewModel, int targetIndex)
    {
        if (_pressedItem is null)
        {
            return;
        }

        var sourceIndex = viewModel.Items.IndexOf(_pressedItem);
        var offsets = StageReorderGeometry.GetPreviewOffsets(
                _reorderCardBounds,
                sourceIndex,
                targetIndex)
            .ToDictionary(static offset => offset.Index, static offset => offset.Offset);
        foreach (var card in _availabilityCards.ToArray())
        {
            if (card.DataContext is not StageItemViewModel item ||
                ReferenceEquals(item, _pressedItem) ||
                TryGetReorderTranslation(card) is not { } translation)
            {
                continue;
            }

            var index = viewModel.Items.IndexOf(item);
            var destination = offsets.GetValueOrDefault(index);
            if (_reorderTranslationTargets.TryGetValue(card, out var current) &&
                current.ItemId == item.Id && AreClose(current.Offset, destination))
            {
                continue;
            }

            _reorderTranslationTargets[card] = new ReorderTranslationTarget(
                item.Id,
                destination);
            AnimateTranslation(translation, destination);
        }
    }

    private void ScheduleReorderVisualRefresh()
    {
        if (_reorderVisualRefreshPending || _reorderGeneration == 0)
        {
            return;
        }

        _reorderVisualRefreshPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _reorderVisualRefreshPending = false;
            if (_reorderGeneration == 0 ||
                DataContext is not StageViewModel viewModel)
            {
                return;
            }

            _reorderCardBounds = GetRealizedReorderCards(viewModel);
            PreviewReorderAt(_lastPointerInViewport);
            AnimateReorderPreview(viewModel, _lastPreviewIndex);
        });
    }

    private static TranslateTransform? TryGetReorderTranslation(Border card) =>
        card.RenderTransform is TransformGroup group
            ? group.Children.OfType<TranslateTransform>().FirstOrDefault()
            : null;

    private static void AnimateTranslation(
        TranslateTransform translation,
        Vector destination)
    {
        var x = new DoubleAnimation(destination.X, ReorderShiftDuration)
        {
            EasingFunction = ReorderShiftEasing,
        };
        var y = new DoubleAnimation(destination.Y, ReorderShiftDuration)
        {
            EasingFunction = ReorderShiftEasing,
        };
        translation.BeginAnimation(
            TranslateTransform.XProperty,
            x,
            HandoffBehavior.SnapshotAndReplace);
        translation.BeginAnimation(
            TranslateTransform.YProperty,
            y,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetTranslation(Border card)
    {
        if (TryGetReorderTranslation(card) is not { } translation)
        {
            return;
        }

        translation.BeginAnimation(TranslateTransform.XProperty, null);
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        translation.X = 0;
        translation.Y = 0;
    }

    private void UpdateAutoScroll(Point pointerInViewport)
    {
        _autoScrollVelocity = StageReorderGeometry.GetAutoScrollVelocity(
            pointerInViewport.Y,
            Math.Max(1, StageItemsViewport.ActualHeight),
            DragEdgeZone,
            MaximumAutoScrollSpeed);
        if (_autoScrollVelocity == 0)
        {
            DetachAutoScroll();
        }
        else if (!_autoScrollAttached)
        {
            _autoScrollAttached = true;
            _lastAutoScrollTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnAutoScrollRendering;
        }
    }

    private void OnAutoScrollRendering(object? sender, EventArgs e)
    {
        if (_reorderGeneration == 0 || _autoScrollVelocity == 0 ||
            GetStageScrollViewer() is not { } scrollViewer)
        {
            DetachAutoScroll();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(
            0.05,
            Stopwatch.GetElapsedTime(_lastAutoScrollTimestamp, now).TotalSeconds);
        _lastAutoScrollTimestamp = now;
        var previousOffset = scrollViewer.VerticalOffset;
        scrollViewer.ScrollToVerticalOffset(
            previousOffset + (_autoScrollVelocity * elapsed));
        if (Math.Abs(previousOffset - scrollViewer.VerticalOffset) > 0.001)
        {
            ScheduleReorderVisualRefresh();
        }
    }

    private void ClearPointerState()
    {
        ClearPointerOwnership();
        ClearReorderVisuals();
    }

    private void ClearPointerOwnership()
    {
        _pressedCard = null;
        _pressedItem = null;
        _reorderGeneration = 0;
        _lastPreviewIndex = -1;
        _reorderCardBounds = [];
        DetachAutoScroll();
    }

    private void ClearReorderVisuals()
    {
        InternalReorderGhost.Visibility = Visibility.Collapsed;
        InternalReorderGhost.DataContext = null;
        foreach (var card in _availabilityCards.ToArray())
        {
            ResetTranslation(card);
        }

        _reorderTranslationTargets.Clear();
    }

    private void ReleasePointerCapture()
    {
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }
    }

    private void DetachAutoScroll()
    {
        if (!_autoScrollAttached)
        {
            return;
        }

        CompositionTarget.Rendering -= OnAutoScrollRendering;
        _autoScrollAttached = false;
        _autoScrollVelocity = 0;
        _lastAutoScrollTimestamp = 0;
    }

    private void OnStageItemCardKeyboardFocusWithinChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is Border card)
        {
            UpdateCardInteractionState(card);
        }
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

    private void OnStageItemContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            SetContextMenuOpenState(menu, isOpen: true);
        }
    }

    private void OnStageItemContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            SetContextMenuOpenState(menu, isOpen: false);
        }
    }

    private void OnStageItemCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        ResetContextMenuState(card);
        _reorderTranslationTargets.Remove(card);
        ResetTranslation(card);
        _availabilityCards.Remove(card);
        CancelAvailabilityBinding(card, resetState: false);
    }

    private void OnStageItemCardDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        ResetContextMenuState(card);
        _reorderTranslationTargets.Remove(card);
        ResetTranslation(card);
        CancelAvailabilityBinding(card, resetState: false);
        ScheduleAvailabilityRefresh();
    }

    private void OnStageItemsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) > 0.001)
        {
            ScheduleCardLayoutRefresh();
        }

        ScheduleAvailabilityRefresh();
        ScheduleReorderVisualRefresh();
        if (!_suppressEdgePrefetchScrollChanged)
        {
            var intent = e.VerticalChange switch
            {
                > 0.001 => StagePageDirection.TowardOlder,
                < -0.001 => StagePageDirection.TowardNewer,
                _ => (StagePageDirection?)null,
            };
            ScheduleEdgePrefetch(intent);
        }
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
            CancelAvailabilityBinding(card, resetState: false);
        }
    }

    private void OnViewIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        TraceStage(IsVisible ? "StageVisible" : "StageHidden");
        if (IsVisible)
        {
            if (_activeRevealGeneration == 0 && _completedRevealGeneration == 0)
            {
                _availabilityRequestsAllowed = true;
            }
            ScheduleAvailabilityRefresh();
            ScheduleEdgePrefetch();
        }
        else
        {
            CancelEdgePrefetch();
            CancelAllInteractions();
            foreach (var card in _availabilityCards.ToArray())
            {
                ResetContextMenuState(card);
            }

            _availabilityRequestsAllowed = false;
            CancelAllAvailabilityBindings(resetState: false);
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
        var scrollViewer = GetStageScrollViewer();
        var viewportWidth = scrollViewer?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        var profile = StageCardLayoutCalculator.Calculate(viewportWidth);
        EnsureViewportWindowCapacity(scrollViewer, profile);
        if (profile == _cardLayoutProfile)
        {
            return;
        }

        var columnsChanged = profile.ColumnCount != _cardLayoutProfile?.ColumnCount;
        if (columnsChanged)
        {
            var cancelledSortInteraction = IsReorderInteractionActive();
            CancelAllInteractions();
            if (cancelledSortInteraction)
            {
                _pendingScrollAnchor = null;
                _scrollResetPending = true;
            }
            else
            {
                _pendingScrollAnchor = CaptureScrollAnchor();
            }

            CancelAllAvailabilityBindings(resetState: false);
        }

        _cardLayoutProfile = profile;
        CardWidth = profile.CardWidth;
        CardRowSurfaceWidth = profile.ColumnCount == 1
            ? profile.CardWidth
            : profile.ColumnCount * (profile.CardWidth + profile.HorizontalGap);

        if (columnsChanged)
        {
            _cardRowsProjection.SetColumnCount(profile.ColumnCount);
        }

        ScheduleAvailabilityRefresh();
        ScheduleEdgePrefetch();
    }

    private void EnsureViewportWindowCapacity(
        ScrollViewer? scrollViewer,
        StageCardLayoutProfile profile)
    {
        if (_observedViewModel is not { } viewModel || scrollViewer is null ||
            !double.IsFinite(scrollViewer.ViewportHeight) ||
            scrollViewer.ViewportHeight <= 0 ||
            !double.IsFinite(CardRowUnitHeight) || CardRowUnitHeight <= 0)
        {
            return;
        }

        var visibleRowCount = checked(
            (int)Math.Ceiling(scrollViewer.ViewportHeight / CardRowUnitHeight));
        var visibleItemCapacity = checked(visibleRowCount * profile.ColumnCount);
        viewModel.EnsureViewportCapacity(Math.Max(1, visibleItemCapacity));
    }

    private ScrollAnchor? CaptureScrollAnchor(
        IReadOnlySet<Guid>? excludedItemIds = null)
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
            .Where(position => excludedItemIds is null ||
                !excludedItemIds.Contains(position.ItemId))
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

    private bool CanPreserveEdgeScrollPosition() =>
        IsLoaded && IsVisible && _availabilityRequestsAllowed &&
        !_externalDragActive && !IsReorderInteractionActive() &&
        _observedViewModel is not null;

    private void ScheduleEdgePrefetch(StagePageDirection? intent = null)
    {
        if (intent is not null)
        {
            _edgePrefetchIntent = intent;
        }

        if (_edgePrefetchActive)
        {
            _edgePrefetchReevaluateAfterCompletion = true;
            return;
        }

        if (_pendingEdgePrefetchEvaluation is
            { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingEdgePrefetchEvaluation = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                _pendingEdgePrefetchEvaluation = null;
                EvaluateEdgePrefetch();
            });
    }

    private void EvaluateEdgePrefetch()
    {
        var intent = _edgePrefetchIntent;
        _edgePrefetchIntent = null;
        if (!CanRequestEdgePrefetch() ||
            GetStageScrollViewer() is not { } scrollViewer ||
            _observedViewModel is not { } viewModel)
        {
            return;
        }

        var threshold = Math.Max(
            EdgePrefetchMinimumRowCount * CardRowUnitHeight,
            scrollViewer.ViewportHeight * EdgePrefetchViewportFraction);
        var nearNewerEdge = scrollViewer.VerticalOffset <= threshold;
        var nearOlderEdge = scrollViewer.ScrollableHeight -
            scrollViewer.VerticalOffset <= threshold;
        var canLoadNewer = nearNewerEdge && viewModel.CanLoadNewer;
        var canLoadOlder = nearOlderEdge && viewModel.CanLoadOlder;

        StagePageDirection? direction;
        if (intent is StagePageDirection.TowardNewer)
        {
            direction = canLoadNewer ? intent : null;
        }
        else if (intent is StagePageDirection.TowardOlder)
        {
            direction = canLoadOlder ? intent : null;
        }
        else if (canLoadNewer ^ canLoadOlder)
        {
            direction = canLoadNewer
                ? StagePageDirection.TowardNewer
                : StagePageDirection.TowardOlder;
        }
        else
        {
            // If both edges fit in the viewport, there is no stable pixel anchor for
            // an automatic window shift. Wait for an explicit wheel direction.
            direction = null;
        }

        if (direction is not null)
        {
            StartEdgePrefetch(viewModel, direction.Value);
        }
    }

    private bool CanRequestEdgePrefetch() =>
        CanPreserveEdgeScrollPosition() &&
        !_scrollResetPending && _pendingScrollAnchor is null &&
        !_rowPublicationRefreshPending &&
        !_cardRowsProjection.HasPendingRebuild &&
        _observedViewModel is
        {
            IsLoading: false,
            IsInteractionLocked: false,
        };

    private void StartEdgePrefetch(
        StageViewModel viewModel,
        StagePageDirection direction)
    {
        if (_edgePrefetchActive)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var contentVersion = _cardRowsProjection.ContentVersion;
        _edgePrefetchCancellation = cancellation;
        _edgePrefetchActive = true;
        _edgePrefetchReevaluateAfterCompletion = false;
        CancelAllAvailabilityBindings(resetState: false);
        EdgePrefetchStarted?.Invoke(direction);
        _ = RunEdgePrefetchAsync(
            viewModel,
            direction,
            contentVersion,
            cancellation);
    }

    private async Task RunEdgePrefetchAsync(
        StageViewModel viewModel,
        StagePageDirection direction,
        long contentVersion,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (direction is StagePageDirection.TowardNewer)
            {
                await viewModel
                    .LoadNewerPageAsync(cancellation.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                await viewModel
                    .LoadOlderPageAsync(cancellation.Token)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The view transition that paused prefetch owns cancellation.
        }
        catch (ObjectDisposedException) when (
            cancellation.IsCancellationRequested ||
            !ReferenceEquals(_observedViewModel, viewModel))
        {
            // The old data context owns its lifetime and can disappear mid-request.
        }
        catch (Exception exception)
        {
            viewModel.RecordDispatchFailure(exception);
        }
        finally
        {
            var contentChanged =
                _cardRowsProjection.ContentVersion != contentVersion;
            var reevaluate = _edgePrefetchReevaluateAfterCompletion;
            if (ReferenceEquals(_edgePrefetchCancellation, cancellation))
            {
                _edgePrefetchCancellation = null;
                _edgePrefetchActive = false;
                _edgePrefetchReevaluateAfterCompletion = false;
            }

            cancellation.Dispose();
            var currentViewModel = _observedViewModel;
            var shouldReevaluateCurrent = reevaluate && currentViewModel is not null &&
                (!ReferenceEquals(currentViewModel, viewModel) ||
                 (!contentChanged && !viewModel.HasError));
            if (shouldReevaluateCurrent)
            {
                ScheduleEdgePrefetch();
            }

            if (!_edgePrefetchActive && _availabilityRequestsAllowed && IsVisible)
            {
                ScheduleAvailabilityRefresh();
            }

            EdgePrefetchCompleted?.Invoke(direction, contentChanged);
        }
    }

    private void CancelEdgePrefetch()
    {
        if (_pendingEdgePrefetchEvaluation is
            { Status: DispatcherOperationStatus.Pending } pending)
        {
            _ = pending.Abort();
        }

        _pendingEdgePrefetchEvaluation = null;
        _edgePrefetchIntent = null;
        _edgePrefetchReevaluateAfterCompletion = false;
        _edgePrefetchCancellation?.Cancel();
    }

    private void ScheduleAvailabilityRefresh()
    {
        if (_availabilityRefreshPending)
        {
            return;
        }

        _availabilityRefreshPending = true;
        TraceStage("StageAvailabilityScheduled");
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _availabilityRefreshPending = false;
            RefreshAvailabilityBindings();
        });
    }

    private void RefreshAvailabilityBindings()
    {
        if (!_availabilityRequestsAllowed || _externalDragActive ||
            IsReorderInteractionActive() || _edgePrefetchActive ||
            _rowPublicationRefreshPending)
        {
            return;
        }

        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(StageItemsList);
        var activeCount = _availabilityOutstandingOperations;
        foreach (var card in _availabilityCards.ToArray())
        {
            if (!IsCardInViewport(card))
            {
                CancelAvailabilityBinding(card, resetState: false);
                continue;
            }

            if (activeCount >= MaximumPendingAvailabilityInspections)
            {
                continue;
            }

            if (EnsureAvailabilityBinding(card))
            {
                activeCount++;
            }
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

    private bool EnsureAvailabilityBinding(Border card)
    {
        if (DataContext is not StageViewModel viewModel ||
            card.DataContext is not StageItemViewModel item)
        {
            CancelAvailabilityBinding(card, resetState: false);
            return false;
        }

        var key = new AvailabilityBindingKey(item.Id, item.Revision);
        if (_availabilityBindings.TryGetValue(card, out var current) &&
            current.Key == key &&
            ReferenceEquals(current.Item, item) &&
            ReferenceEquals(current.ViewModel, viewModel))
        {
            return false;
        }

        CancelAvailabilityBinding(card, resetState: false);
        if (item.HasAvailabilityResult &&
            (_availabilityRefreshGeneration == 0 ||
             (_availabilityValidatedGenerations.TryGetValue(item, out var generation) &&
              generation == _availabilityRefreshGeneration)))
        {
            return false;
        }

        var binding = new AvailabilityBinding(viewModel, item, key)
        {
            TraceId = _revealTrace.NextId(),
            TraceGeneration = _diagnosticGeneration,
        };
        _availabilityBindings.Add(card, binding);
        _availabilityOutstandingOperations++;
        _revealTrace.Record("StageAvailabilityRequested", binding.TraceGeneration,
            _revealSourceId, binding.TraceId, item.Id, item.Revision);
        try
        {
            _ = CompleteAvailabilityBindingAsync(
                card,
                binding,
                viewModel.InspectAvailabilityAsync(item, binding.CancellationToken));
        }
        catch (Exception exception)
        {
            _ = CompleteAvailabilityBindingAsync(
                card,
                binding,
                Task.FromException<StageFileAvailability>(exception));
        }
        return true;
    }

    private async Task CompleteAvailabilityBindingAsync(
        Border card,
        AvailabilityBinding binding,
        Task<StageFileAvailability> operation)
    {
        try
        {
            var availability = await operation.ConfigureAwait(true);
            _revealTrace.Record("StageAvailabilityReturned", binding.TraceGeneration,
                _revealSourceId, binding.TraceId, binding.Key.ItemId, binding.Key.Revision,
                value1: (int)availability);
            if (IsCurrentAvailabilityBinding(card, binding))
            {
                _revealTrace.Record("StageAvailabilityApplied", binding.TraceGeneration,
                    _revealSourceId, binding.TraceId, binding.Key.ItemId, binding.Key.Revision);
                _ = binding.Item.ApplyAvailability(
                    binding.Key.Revision,
                    availability);
                _availabilityValidatedGenerations[binding.Item] =
                    _availabilityRefreshGeneration;
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
                _availabilityValidatedGenerations[binding.Item] =
                    _availabilityRefreshGeneration;
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentAvailabilityBinding(card, binding))
            {
                _ = binding.Item.ApplyAvailability(
                    binding.Key.Revision,
                    StageFileAvailability.Unavailable);
                _availabilityValidatedGenerations[binding.Item] =
                    _availabilityRefreshGeneration;
            }

            Trace.TraceWarning(
                "Stage file availability inspection failed with {0}.",
                exception.GetType().Name);
        }
        finally
        {
            _revealTrace.Record("StageAvailabilityCompleted", binding.TraceGeneration,
                _revealSourceId, binding.TraceId, binding.Key.ItemId, binding.Key.Revision);
            binding.CompleteOperation();
            _availabilityOutstandingOperations = Math.Max(
                0,
                _availabilityOutstandingOperations - 1);
            if (_availabilityRequestsAllowed)
            {
                ScheduleAvailabilityRefresh();
            }
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
        _revealTrace.Record("StageAvailabilityDetached", binding.TraceGeneration,
            _revealSourceId, binding.TraceId, binding.Key.ItemId, binding.Key.Revision,
            value1: resetState ? 1 : 0, value2: binding.IsOperationCompleted ? 1 : 0);
        if (resetState)
        {
            binding.Item.ResetAvailability(binding.Key.Revision);
        }
    }

    private void CancelAllAvailabilityBindings(bool resetState = false)
    {
        foreach (var card in _availabilityBindings.Keys.ToArray())
        {
            CancelAvailabilityBinding(card, resetState);
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

    private void SetContextMenuOpenState(ContextMenu menu, bool isOpen)
    {
        if (menu.PlacementTarget is not DependencyObject placementTarget ||
            FindVisualAncestor<Border>(placementTarget, "StageItemCard") is not { } card)
        {
            return;
        }

        SetIsContextMenuOpen(card, isOpen);
        if (FindVisualAncestor<ContentPresenter>(card) is { } container)
        {
            SetIsContextMenuOpen(container, isOpen);
        }

        UpdateCardInteractionState(card);
    }

    private void ResetContextMenuState(Border card)
    {
        foreach (var button in FindNamedVisualDescendants<Button>(
                     card,
                     "StageItemMenuButton"))
        {
            if (button.ContextMenu is { IsOpen: true } menu)
            {
                menu.IsOpen = false;
            }
        }

        SetIsContextMenuOpen(card, false);
        if (FindVisualAncestor<ContentPresenter>(card) is { } container)
        {
            SetIsContextMenuOpen(container, false);
            SetIsInteractionActive(container, false);
        }

        SetIsInteractionActive(card, false);
        SetIsKeyboardContextActive(card, false);
    }

    private void UpdateCardInteractionState(Border card)
    {
        var canPresentInteraction = IsVisible && !_externalDragActive &&
            _observedViewModel?.IsInteractionLocked != true &&
            _pressedItem is null && card.IsVisible;
        var keyboardContextActive = canPresentInteraction &&
            (card.IsKeyboardFocusWithin || GetIsContextMenuOpen(card));
        var interactionActive = canPresentInteraction &&
            (card.IsMouseOver || keyboardContextActive);

        SetIsInteractionActive(card, interactionActive);
        SetIsKeyboardContextActive(card, keyboardContextActive);
        if (FindVisualAncestor<ContentPresenter>(card) is { } container)
        {
            SetIsInteractionActive(container, interactionActive);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject start, string? name = null)
        where T : FrameworkElement
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match &&
                (name is null || string.Equals(match.Name, name, StringComparison.Ordinal)))
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsCardDragSurface(DependencyObject source, Border card)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, card))
        {
            if (current is ButtonBase or MenuItem or TextBoxBase)
            {
                return false;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return ReferenceEquals(current, card);
    }

    private bool IsReorderInteractionActive() =>
        _pressedItem is not null ||
        _observedViewModel is { IsReordering: true } or { IsCommitting: true };

    private static CubicEase CreateReorderShiftEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    private static bool AreClose(Vector left, Vector right) =>
        Math.Abs(left.X - right.X) <= 0.001 &&
        Math.Abs(left.Y - right.Y) <= 0.001;

    private readonly record struct ScrollAnchor(Guid ItemId, double RelativeTop);

    private readonly record struct VisibleCardPosition(
        Guid ItemId,
        double Top,
        double Left);

    private readonly record struct ReorderTranslationTarget(
        Guid ItemId,
        Vector Offset);

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
        public long TraceId { get; init; }
        public long TraceGeneration { get; init; }
        public bool IsOperationCompleted => Volatile.Read(ref _operationCompleted) != 0;

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
