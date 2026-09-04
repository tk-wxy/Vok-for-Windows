using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Vok.Features.Launcher;

public partial class LauncherView : UserControl
{
    private const double DragEdgeZone = 36;
    private const double MaximumAutoScrollSpeed = 720;
    private static readonly TimeSpan LaunchFeedbackDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan LaunchFeedbackWatchdog = TimeSpan.FromMilliseconds(500);

    public static readonly DependencyProperty CardSurfaceHeightProperty =
        DependencyProperty.Register(
            nameof(CardSurfaceHeight),
            typeof(double),
            typeof(LauncherView),
            new PropertyMetadata(94.0));

    public static readonly DependencyProperty CardUnitHeightProperty =
        DependencyProperty.Register(
            nameof(CardUnitHeight),
            typeof(double),
            typeof(LauncherView),
            new PropertyMetadata(100.0));

    public static readonly DependencyProperty VisibleRowCountProperty =
        DependencyProperty.Register(
            nameof(VisibleRowCount),
            typeof(int),
            typeof(LauncherView),
            new PropertyMetadata(1));

    private LauncherLayoutProfileCache? _layoutProfiles;
    private bool _pageLoadRequested;
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private Button? _pressedTile;
    private LauncherItemViewModel? _pressedItem;
    private Point _pressPoint;
    private Point _dragPointerOffset;
    private Point _lastPointerInViewport;
    private bool _dragThresholdExceeded;
    private LauncherReorderSession? _reorderSession;
    private ScrollViewer? _launcherScrollViewer;
    private bool _autoScrollAttached;
    private double _autoScrollVelocity;
    private long _lastAutoScrollTimestamp;
    private readonly LauncherLaunchFeedbackGate _launchFeedbackGate = new();
    private LauncherItemViewModel? _pendingLaunchItem;
    private ScaleTransform? _pendingLaunchScale;
    private DispatcherTimer? _launchWatchdog;

    public LauncherView()
    {
        InitializeComponent();
        LauncherItemsViewport.SizeChanged += OnLauncherItemsViewportSizeChanged;
        LauncherItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnLauncherScrollChanged));
        IsVisibleChanged += OnLauncherIsVisibleChanged;
    }

    public double CardSurfaceHeight
    {
        get => (double)GetValue(CardSurfaceHeightProperty);
        private set => SetCurrentValue(CardSurfaceHeightProperty, value);
    }

    public double CardUnitHeight
    {
        get => (double)GetValue(CardUnitHeightProperty);
        private set => SetCurrentValue(CardUnitHeightProperty, value);
    }

    public int VisibleRowCount
    {
        get => (int)GetValue(VisibleRowCountProperty);
        private set => SetCurrentValue(VisibleRowCountProperty, value);
    }

    internal void UseLayoutProfiles(LauncherLayoutProfileCache layoutProfiles)
    {
        ArgumentNullException.ThrowIfNull(layoutProfiles);
        _layoutProfiles = layoutProfiles;
        ApplyLayoutProfile(LauncherItemsViewport.ActualHeight);
    }

    internal void SetExternalDragActive(bool isActive, bool canDrop)
    {
        if (_externalDragActive == isActive && _externalDragTargeted == canDrop)
        {
            return;
        }

        _externalDragActive = isActive;
        _externalDragTargeted = canDrop;
        DropOverlay.Visibility = isActive && canDrop
            ? Visibility.Visible
            : Visibility.Collapsed;
        LauncherItemsList.IsHitTestVisible = !isActive;
    }

    internal bool TryCancelReorderInteraction()
    {
        if (_reorderSession is null)
        {
            return false;
        }

        CancelPointerInteraction();
        return true;
    }

    internal void CancelAllInteractions()
    {
        CancelPointerInteraction();
        CancelPendingLaunch();
    }

    private void OnTilePreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Button tile ||
            tile.DataContext is not LauncherItemViewModel item ||
            DataContext is not LauncherViewModel viewModel ||
            _externalDragActive ||
            _launchFeedbackGate.HasActive ||
            viewModel.IsCatalogBusy ||
            viewModel.IsReordering ||
            item.IsBusy)
        {
            e.Handled = true;
            return;
        }

        CancelPointerInteraction();
        _pressedTile = tile;
        _pressedItem = item;
        _pressPoint = e.GetPosition(this);
        _dragPointerOffset = e.GetPosition(tile);
        _dragThresholdExceeded = false;
        e.Handled = true;
        if (!Mouse.Capture(this, CaptureMode.Element))
        {
            ClearPointerState();
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTile is null || _pressedItem is null)
        {
            return;
        }

        e.Handled = true;
        if (e.LeftButton is not MouseButtonState.Pressed)
        {
            CancelPointerInteraction();
            return;
        }

        var pointer = e.GetPosition(this);
        if (_reorderSession is null)
        {
            var delta = pointer - _pressPoint;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragThresholdExceeded = true;
            if (DataContext is not LauncherViewModel viewModel ||
                viewModel.BeginReorder(_pressedItem) is not { } session)
            {
                return;
            }

            _reorderSession = session;
            ShowInternalDragGhost(_pressedTile, _pressedItem);
        }

        UpdateInternalDrag(pointer, e.GetPosition(LauncherItemsViewport));
    }

    private async void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedTile is null || _pressedItem is null)
        {
            return;
        }

        e.Handled = true;
        var tile = _pressedTile;
        var item = _pressedItem;
        var shouldLaunch = !_dragThresholdExceeded && IsPointerInside(tile, e);
        var reorder = _reorderSession;
        ClearPointerState();
        ReleasePointerCapture();

        if (reorder is not null && DataContext is LauncherViewModel viewModel)
        {
            try
            {
                await viewModel.CommitReorderAsync(reorder).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The view-model lifetime owns rollback when the application exits.
            }

            return;
        }

        if (shouldLaunch)
        {
            BeginLaunchFeedback(tile, item);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_reorderSession is null || GetLauncherScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        e.Handled = true;
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset - (e.Delta / 3.0));
        PreviewReorderAt(_lastPointerInViewport);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressedItem is not null)
        {
            CancelPointerInteraction();
        }
    }

    private void OnLauncherIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CancelAllInteractions();
        }
    }

    private void UpdateInternalDrag(Point pointerInView, Point pointerInViewport)
    {
        _lastPointerInViewport = pointerInViewport;
        PositionInternalDragGhost(pointerInView);
        UpdateAutoScroll(pointerInViewport);
        PreviewReorderAt(pointerInViewport);
    }

    private void PreviewReorderAt(Point pointerInViewport)
    {
        if (_reorderSession is null ||
            DataContext is not LauncherViewModel viewModel ||
            viewModel.Items.Count == 0)
        {
            return;
        }

        var usableWidth = Math.Max(1, LauncherItemsList.ActualWidth);
        var verticalOffset = GetLauncherScrollViewer()?.VerticalOffset ?? 0;
        var targetIndex = LauncherReorderGeometry.GetTargetIndex(
            pointerInViewport.X,
            pointerInViewport.Y,
            usableWidth,
            verticalOffset,
            CardUnitHeight,
            viewModel.Items.Count);
        _ = viewModel.PreviewReorder(_reorderSession, targetIndex);
    }

    private void ShowInternalDragGhost(Button tile, LauncherItemViewModel item)
    {
        InternalDragGhost.DataContext = item;
        InternalDragGhost.Width = Math.Max(1, tile.ActualWidth);
        InternalDragGhost.Height = Math.Max(1, tile.ActualHeight);
        InternalDragGhost.Visibility = Visibility.Visible;
    }

    private void PositionInternalDragGhost(Point pointer)
    {
        if (InternalDragGhost.Visibility is not Visibility.Visible)
        {
            return;
        }

        var left = Math.Clamp(
            pointer.X - _dragPointerOffset.X,
            0,
            Math.Max(0, ActualWidth - InternalDragGhost.Width));
        var top = Math.Clamp(
            pointer.Y - _dragPointerOffset.Y,
            0,
            Math.Max(0, ActualHeight - InternalDragGhost.Height));
        Canvas.SetLeft(InternalDragGhost, left);
        Canvas.SetTop(InternalDragGhost, top);
    }

    private void UpdateAutoScroll(Point pointerInViewport)
    {
        _autoScrollVelocity = LauncherReorderGeometry.GetAutoScrollVelocity(
            pointerInViewport.Y,
            Math.Max(1, LauncherItemsViewport.ActualHeight),
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
        if (_reorderSession is null ||
            _autoScrollVelocity == 0 ||
            GetLauncherScrollViewer() is not { } scrollViewer)
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
            PreviewReorderAt(_lastPointerInViewport);
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

    internal void BeginLaunchFeedback(Button tile, LauncherItemViewModel item)
    {
        if (DataContext is not LauncherViewModel viewModel ||
            item.IsBusy ||
            viewModel.IsCatalogBusy ||
            viewModel.IsReordering)
        {
            return;
        }

        CancelPendingLaunch();
        if (!SystemParameters.ClientAreaAnimation)
        {
            _ = viewModel.OpenCommand.ExecuteAsync(item);
            return;
        }

        _ = tile.ApplyTemplate();
        if (tile.Template.FindName("CardScale", tile) is not ScaleTransform scale)
        {
            _ = viewModel.OpenCommand.ExecuteAsync(item);
            return;
        }

        var generation = _launchFeedbackGate.Begin();
        _pendingLaunchItem = item;
        _pendingLaunchScale = scale;
        var scaleX = CreateLaunchFeedbackAnimation();
        var scaleY = CreateLaunchFeedbackAnimation();
        scaleX.Completed += (_, _) => CompletePendingLaunch(generation);
        _launchWatchdog = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = LaunchFeedbackWatchdog,
        };
        _launchWatchdog.Tick += OnLaunchWatchdogTick;
        _launchWatchdog.Start();
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            scaleX,
            HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            scaleY,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames CreateLaunchFeedbackAnimation()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(LaunchFeedbackDuration),
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            0.94,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)),
            new KeySpline(0.2, 0, 0, 1)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(LaunchFeedbackDuration),
            new KeySpline(0.2, 0, 0, 1)));
        return animation;
    }

    private void OnLaunchWatchdogTick(object? sender, EventArgs e)
    {
        var generation = _launchFeedbackGate.ActiveGeneration;
        if (generation != 0)
        {
            CompletePendingLaunch(generation);
        }
        else
        {
            StopLaunchFeedbackVisual();
        }
    }

    private async void CompletePendingLaunch(long generation)
    {
        if (!_launchFeedbackGate.TryClaim(generation))
        {
            return;
        }

        // Claim before clearing animation clocks or dispatching the command;
        // Completed and the watchdog cannot both launch the same item.
        var item = _pendingLaunchItem;
        _pendingLaunchItem = null;
        StopLaunchFeedbackVisual();
        if (item is null ||
            !IsVisible ||
            DataContext is not LauncherViewModel viewModel)
        {
            return;
        }

        await viewModel.OpenCommand.ExecuteAsync(item).ConfigureAwait(true);
    }

    private void CancelPendingLaunch()
    {
        _launchFeedbackGate.Cancel();
        _pendingLaunchItem = null;
        StopLaunchFeedbackVisual();
    }

    private void StopLaunchFeedbackVisual()
    {
        if (_launchWatchdog is not null)
        {
            _launchWatchdog.Stop();
            _launchWatchdog.Tick -= OnLaunchWatchdogTick;
            _launchWatchdog = null;
        }

        if (_pendingLaunchScale is not null)
        {
            _pendingLaunchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _pendingLaunchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _pendingLaunchScale = null;
        }
    }

    private void CancelPointerInteraction()
    {
        var reorder = _reorderSession;
        ClearPointerState();
        ReleasePointerCapture();
        if (reorder is not null && DataContext is LauncherViewModel viewModel)
        {
            _ = viewModel.CancelReorder(reorder);
        }
    }

    private void ClearPointerState()
    {
        _pressedTile = null;
        _pressedItem = null;
        _reorderSession = null;
        _dragThresholdExceeded = false;
        InternalDragGhost.Visibility = Visibility.Collapsed;
        InternalDragGhost.DataContext = null;
        DetachAutoScroll();
    }

    private void ReleasePointerCapture()
    {
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }
    }

    private static bool IsPointerInside(Button tile, MouseEventArgs e)
    {
        var point = e.GetPosition(tile);
        return point.X >= 0 && point.Y >= 0 &&
            point.X <= tile.ActualWidth && point.Y <= tile.ActualHeight;
    }

    private ScrollViewer? GetLauncherScrollViewer() =>
        _launcherScrollViewer ??= FindVisualDescendant<ScrollViewer>(LauncherItemsList);

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

    private void OnLauncherItemsViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyLayoutProfile(e.NewSize.Height);

    private async void OnLauncherScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_pageLoadRequested ||
            DataContext is not LauncherViewModel viewModel ||
            viewModel.IsReordering ||
            e.ExtentHeight <= 0 ||
            e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - CardUnitHeight)
        {
            return;
        }

        _pageLoadRequested = true;
        try
        {
            await viewModel.LoadNextPageAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // The application is closing; the view model owns cancellation and cleanup.
        }
        catch (OperationCanceledException)
        {
            // The view-model lifetime ended while an incremental page was pending.
        }
        finally
        {
            _pageLoadRequested = false;
        }
    }

    private void ApplyLayoutProfile(double viewportHeight)
    {
        if (_layoutProfiles is null || viewportHeight <= 0)
        {
            return;
        }

        var profile = _layoutProfiles.GetOrCreateForViewport(viewportHeight);
        CardSurfaceHeight = profile.CardSurfaceHeight;
        CardUnitHeight = profile.CardUnitHeight;
        VisibleRowCount = profile.VisibleRowCount;
    }
}
