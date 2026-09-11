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
            new PropertyMetadata(StageCardLayoutCalculator.DefaultRowUnitHeight));

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
    private StageCardLayoutProfile? _cardLayoutProfile;
    private ScrollAnchor? _pendingScrollAnchor;
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private bool _scrollResetPending;
    private bool _cardLayoutRefreshPending;
    private bool _rowPublicationRefreshPending;
    private bool _availabilityRefreshPending;
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

    internal StageCardLayoutProfile? CardLayoutProfile => _cardLayoutProfile;

    internal double CardViewportWidth => GetStageScrollViewer()?.ViewportWidth ?? 0;

    internal int ActiveAvailabilityBindingCount => _availabilityBindings.Count;

    internal int TrackedAvailabilityCardCount => _availabilityCards.Count;

    internal bool HasPendingDeferredWork =>
        _cardLayoutRefreshPending ||
        _rowPublicationRefreshPending ||
        _availabilityRefreshPending ||
        _reorderVisualRefreshPending ||
        _cardRowsProjection.HasPendingRebuild;

    internal bool HasActiveReorderPointer => _pressedItem is not null;

    internal bool HasAutoScrollSubscription => _autoScrollAttached;

    internal void SetExternalDragState(bool sessionActive, bool targeted)
    {
        if (sessionActive)
        {
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
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        CancelAllInteractions(_observedViewModel);
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
        CancelAllInteractions();
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
        viewModel.PropertyChanged += OnObservedViewModelPropertyChanged;
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
            _observedViewModel.PropertyChanged -= OnObservedViewModelPropertyChanged;
            _observedViewModel = null;
        }

        CancelAllAvailabilityBindings();
        _cardRowsProjection.Attach(null);
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
        }
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
            if (_reorderGeneration != 0)
            {
                ScheduleReorderVisualRefresh();
            }
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
        if (_reorderGeneration == 0 || GetStageScrollViewer() is not { } scrollViewer)
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
        CancelAvailabilityBinding(card, resetState: !IsReorderInteractionActive());
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
        CancelAvailabilityBinding(card, resetState: !IsReorderInteractionActive());
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
            CancelAvailabilityBinding(card, resetState: !IsReorderInteractionActive());
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
            CancelAllInteractions();
            foreach (var card in _availabilityCards.ToArray())
            {
                ResetContextMenuState(card);
            }

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

            CancelAllAvailabilityBindings();
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
        if (item.HasAvailabilityResult)
        {
            return;
        }

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
