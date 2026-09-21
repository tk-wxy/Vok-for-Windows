using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Vok.Infrastructure.Windows.Stage;

namespace Vok.StageScaleProbe;

internal sealed class StageScaleRunner(
    Application application,
    ProbeOptions options,
    ProbeMarker marker)
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);
    private readonly List<EdgeStepSample> _edgeSamples = [];
    private readonly HashSet<Guid> _visited = [];
    private bool _orderingValid = true;
    private bool _windowBoundValid = true;
    private int _maximumWindowItemCount;
    private int _maximumRealizedCardCount;
    private int _anchorMeasurements;
    private double _maximumAnchorErrorDip;
    private bool _reachedOldest;
    private bool _returnedToNewest;

    internal async Task<RunProbeReport> RunAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var databaseBefore = StageScaleData.AuditDatabase(
            options.DatabasePath,
            marker.StageDigest);
        var sourceHashBefore = StageScaleData.ComputeFileSha256(options.SourcePath);
        var (executablePath, executableHash) = StageScaleData.GetExecutableIdentity();
        var frameSampler = new FrameIntervalSampler();
        await using var resourceSampler = new ResourceSampler();
        resourceSampler.SetPhase("view-model-initialize");

        new WpfAppThemeApplier(application).Apply(AppThemeMode.Light);
        using var repository = new SqliteStageRepository(
            options.DatabasePath,
            new StageOptions(checked((long)options.ItemCount + 1)));
        using var fileService = new WindowsStageFileService();
        using var viewModel = new StageViewModel(repository, fileService);

        var initializeStopwatch = Stopwatch.StartNew();
        await viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);
        initializeStopwatch.Stop();
        if (viewModel.HasError || viewModel.TotalCount != options.ItemCount)
        {
            throw new InvalidOperationException(
                $"The initial Stage load failed: error={viewModel.HasError}; " +
                $"total={viewModel.TotalCount}; expected={options.ItemCount}.");
        }

        var view = new StageView
        {
            DataContext = viewModel,
        };
        var window = new Window
        {
            Title = $"Vok Stage Scale Probe — {options.ItemCount:N0}",
            Width = options.WindowWidth,
            Height = options.WindowHeight,
            Content = view,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.SetResourceReference(
            Control.BackgroundProperty,
            "ApplicationBackgroundBrush");

        frameSampler.Start();
        resourceSampler.SetPhase("first-viewport");
        var firstViewportStopwatch = Stopwatch.StartNew();
        window.Show();
        ScrollViewer? scrollViewer = null;
        AnchorObserver? anchorObserver = null;
        DisplaySnapshot? display = null;
        var deferredWorkDrained = false;
        try
        {
            await WaitForConditionAsync(
                () =>
                {
                    window.UpdateLayout();
                    scrollViewer = FindVisualDescendant<ScrollViewer>(view);
                    return scrollViewer is not null &&
                        view.CardLayoutProfile is not null &&
                        scrollViewer.ViewportHeight > 0;
                },
                "The real Stage ScrollViewer did not finish layout.",
                cancellationToken).ConfigureAwait(true);
            anchorObserver = new AnchorObserver(view, viewModel, scrollViewer!);
            await WaitForViewportReadyAsync(
                    view,
                    viewModel,
                    cancellationToken)
                .ConfigureAwait(true);
            firstViewportStopwatch.Stop();
            display = DisplayMetrics.Capture(window, scrollViewer!, view);
            ObserveCurrentWindow(view, viewModel);
            resourceSampler.CaptureCheckpoint("first-viewport-settled");

            if (options.EdgeSteps == 0)
            {
                await RunFullRoundTripAsync(
                        window,
                        view,
                        viewModel,
                        scrollViewer!,
                        anchorObserver,
                        resourceSampler,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            else
            {
                await RunPressureTraversalAsync(
                        window,
                        view,
                        viewModel,
                        scrollViewer!,
                        anchorObserver,
                        resourceSampler,
                        cancellationToken)
                    .ConfigureAwait(true);
            }

            await WaitForViewportReadyAsync(
                    view,
                    viewModel,
                    cancellationToken)
                .ConfigureAwait(true);
            window.Hide();
            await WaitForConditionAsync(
                () => !view.HasActiveEdgePrefetch &&
                    !view.HasPendingDeferredWork &&
                    view.OutstandingAvailabilityOperationCount == 0,
                "Stage work did not drain after the probe window was hidden.",
                cancellationToken).ConfigureAwait(true);
            deferredWorkDrained = true;
            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
            resourceSampler.CaptureCheckpoint("hidden-recovery", _edgeSamples.Count);
        }
        finally
        {
            anchorObserver?.Dispose();
            frameSampler.Stop();
            window.Close();
        }

        await resourceSampler.DisposeAsync().ConfigureAwait(true);
        var resourceSamples = resourceSampler.Samples;
        var databaseAfter = StageScaleData.AuditDatabase(options.DatabasePath);
        var sourceHashAfter = StageScaleData.ComputeFileSha256(options.SourcePath);
        if (!string.Equals(
                databaseAfter.StageDigest,
                marker.StageDigest,
                StringComparison.Ordinal) ||
            databaseAfter.StageCount != databaseBefore.StageCount ||
            !string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The isolated database or probe-owned source changed during the read-only run.");
        }

        var traversal = BuildTraversalSummary(
            deferredWorkDrained);
        if (!traversal.OrderingValid || !traversal.WindowBoundValid ||
            !traversal.DeferredWorkDrained || traversal.MaximumAnchorErrorDip > 1.0)
        {
            throw new InvalidOperationException(
                "The scale traversal violated an ordering, window, lifecycle, or anchor gate.");
        }

        if (traversal.CompletedEdgeSteps > 0 &&
            traversal.AnchorMeasurements != traversal.CompletedEdgeSteps)
        {
            throw new InvalidOperationException(
                "At least one rolling-window transition lacked a measurable retained pixel anchor.");
        }

        if (options.EdgeSteps == 0 && options.ItemCount > StageViewModel.PageSize &&
            (traversal.VisitedOccurrenceCount != options.ItemCount ||
             !traversal.ReachedOldest || !traversal.ReturnedToNewest))
        {
            throw new InvalidOperationException(
                "The full round trip did not cover every occurrence and return to newest.");
        }

        return new RunProbeReport(
            ReportVersion: 1,
            Status: "Succeeded",
            Mode: "Run",
            StartedUtc: started.ToString("O", CultureInfo.InvariantCulture),
            CompletedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SourceRevision: options.SourceRevision,
            ExecutablePath: executablePath,
            ExecutableSha256: executableHash,
            Root: options.Root,
            RequestedItemCount: options.ItemCount,
            ViewModelInitializeMilliseconds: initializeStopwatch.Elapsed.TotalMilliseconds,
            FirstSettledViewportMilliseconds: firstViewportStopwatch.Elapsed.TotalMilliseconds,
            Display: display ?? throw new InvalidOperationException("Display metrics are missing."),
            DatabaseBefore: databaseBefore,
            DatabaseAfter: databaseAfter,
            SourceSha256Before: sourceHashBefore,
            SourceSha256After: sourceHashAfter,
            Traversal: traversal,
            Frames: frameSampler.Summarize(),
            Resources: ResourceSampler.Summarize(resourceSamples),
            EdgeSamples: _edgeSamples,
            ResourceSamples: resourceSamples);
    }

    private async Task RunFullRoundTripAsync(
        Window window,
        StageView view,
        StageViewModel viewModel,
        ScrollViewer scrollViewer,
        AnchorObserver anchorObserver,
        ResourceSampler resourceSampler,
        CancellationToken cancellationToken)
    {
        var maximumStepsPerDirection = checked(
            (options.ItemCount + StageViewModel.PageSize - 1) /
            StageViewModel.PageSize + 4);
        var steps = 0;
        while (viewModel.CanLoadOlder)
        {
            if (++steps > maximumStepsPerDirection)
            {
                throw new InvalidOperationException(
                    "Older traversal exceeded its bounded segment count.");
            }

            await ExecuteEdgeStepAsync(
                    StagePageDirection.TowardOlder,
                    window,
                    view,
                    viewModel,
                    scrollViewer,
                    anchorObserver,
                    resourceSampler,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        _reachedOldest = true;

        steps = 0;
        while (viewModel.CanLoadNewer)
        {
            if (++steps > maximumStepsPerDirection)
            {
                throw new InvalidOperationException(
                    "Newer traversal exceeded its bounded segment count.");
            }

            await ExecuteEdgeStepAsync(
                    StagePageDirection.TowardNewer,
                    window,
                    view,
                    viewModel,
                    scrollViewer,
                    anchorObserver,
                    resourceSampler,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        _returnedToNewest = true;
    }

    private async Task RunPressureTraversalAsync(
        Window window,
        StageView view,
        StageViewModel viewModel,
        ScrollViewer scrollViewer,
        AnchorObserver anchorObserver,
        ResourceSampler resourceSampler,
        CancellationToken cancellationToken)
    {
        var random = new Random(0x564F4B);
        for (var step = 0; step < options.EdgeSteps; step++)
        {
            var direction = SelectPressureDirection(viewModel, random);
            if (direction is null)
            {
                break;
            }

            await ExecuteEdgeStepAsync(
                    direction.Value,
                    window,
                    view,
                    viewModel,
                    scrollViewer,
                    anchorObserver,
                    resourceSampler,
                    cancellationToken)
                .ConfigureAwait(true);
            _reachedOldest |= !viewModel.CanLoadOlder;
            _returnedToNewest |= !viewModel.CanLoadNewer &&
                direction is StagePageDirection.TowardNewer;
        }
    }

    private static StagePageDirection? SelectPressureDirection(
        StageViewModel viewModel,
        Random random)
    {
        if (!viewModel.CanLoadOlder && !viewModel.CanLoadNewer)
        {
            return null;
        }

        if (!viewModel.CanLoadNewer)
        {
            return StagePageDirection.TowardOlder;
        }

        if (!viewModel.CanLoadOlder)
        {
            return StagePageDirection.TowardNewer;
        }

        return random.Next(100) < 70
            ? StagePageDirection.TowardOlder
            : StagePageDirection.TowardNewer;
    }

    private async Task ExecuteEdgeStepAsync(
        StagePageDirection direction,
        Window window,
        StageView view,
        StageViewModel viewModel,
        ScrollViewer scrollViewer,
        AnchorObserver anchorObserver,
        ResourceSampler resourceSampler,
        CancellationToken cancellationToken)
    {
        var before = WindowSignature.Capture(viewModel);
        var step = _edgeSamples.Count + 1;
        resourceSampler.SetPhase(direction.ToString(), step);
        var stopwatch = Stopwatch.StartNew();
        anchorObserver.BeginStepTiming();
        var viewportMutationVersion = anchorObserver.ViewportMutationVersion;
        await MoveToEdgeAsync(scrollViewer, view.CardRowUnitHeight, direction)
            .ConfigureAwait(true);
        await anchorObserver
            .WaitForViewportMutationAsync(
                viewportMutationVersion,
                SettleTimeout,
                cancellationToken)
            .ConfigureAwait(true);
        await WaitForViewportReadyAsync(view, viewModel, cancellationToken)
            .ConfigureAwait(true);

        stopwatch.Stop();
        var timing = anchorObserver.EndStepTiming();
        var after = WindowSignature.Capture(viewModel);
        if (after == before)
        {
            throw new InvalidOperationException(
                $"The WPF {direction} edge did not advance the rolling window.");
        }

        ObserveCurrentWindow(view, viewModel);
        var anchorError = anchorObserver.ConsumeAnchorErrorDip();
        if (anchorError is not null)
        {
            _anchorMeasurements++;
            _maximumAnchorErrorDip = Math.Max(
                _maximumAnchorErrorDip,
                anchorError.Value);
        }

        var realizedCards = CountRealizedCards(view);
        _maximumRealizedCardCount = Math.Max(_maximumRealizedCardCount, realizedCards);
        _edgeSamples.Add(new EdgeStepSample(
            Step: step,
            Direction: direction.ToString(),
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            ProjectionChangingMilliseconds: timing.ProjectionChangingMilliseconds,
            ItemsChangedMilliseconds: timing.ItemsChangedMilliseconds,
            RowsChangedMilliseconds: timing.RowsChangedMilliseconds,
            ViewportSettledMilliseconds: timing.ViewportSettledMilliseconds,
            WindowItemCount: viewModel.Items.Count,
            WindowMaximumItemCount: viewModel.WindowMaximumItemCount,
            CardRowCount: view.CardRows.Count,
            RealizedCardCount: realizedCards,
            VerticalOffset: scrollViewer.VerticalOffset,
            ScrollableHeight: scrollViewer.ScrollableHeight,
            AnchorErrorDip: anchorError,
            FirstItemId: viewModel.Items[0].Id,
            LastItemId: viewModel.Items[^1].Id,
            CanLoadNewer: viewModel.CanLoadNewer,
            CanLoadOlder: viewModel.CanLoadOlder));
        resourceSampler.CaptureCheckpoint("edge-settled", step);
        if ((step % 25) == 0)
        {
            Console.WriteLine(
                $"edge {step}: {direction}; visited={_visited.Count}; " +
                $"window={viewModel.Items.Count}/{viewModel.WindowMaximumItemCount}");
        }
    }

    private void ObserveCurrentWindow(StageView view, StageViewModel viewModel)
    {
        var items = viewModel.Items;
        if (items.Count == 0)
        {
            throw new InvalidOperationException("The Stage window unexpectedly became empty.");
        }

        foreach (var item in items)
        {
            _visited.Add(item.Id);
        }

        _orderingValid &= IsStrictlyDescending(items);
        _windowBoundValid &= items.Count <= viewModel.WindowMaximumItemCount;
        _maximumWindowItemCount = Math.Max(_maximumWindowItemCount, items.Count);
        _maximumRealizedCardCount = Math.Max(
            _maximumRealizedCardCount,
            CountRealizedCards(view));
        if (!_orderingValid || !_windowBoundValid)
        {
            throw new InvalidOperationException(
                "The Stage rolling window lost ordering or exceeded its configured bound.");
        }
    }

    private TraversalSummary BuildTraversalSummary(bool deferredWorkDrained)
    {
        var elapsed = _edgeSamples
            .Select(static sample => sample.ElapsedMilliseconds)
            .OrderBy(static value => value)
            .ToArray();
        return new TraversalSummary(
            Workload: options.EdgeSteps == 0 ? "FullRoundTrip" : "DeterministicPressure",
            RequestedEdgeSteps: options.EdgeSteps,
            CompletedEdgeSteps: _edgeSamples.Count,
            OlderSteps: _edgeSamples.Count(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardOlder)),
            NewerSteps: _edgeSamples.Count(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardNewer)),
            VisitedOccurrenceCount: _visited.Count,
            ReachedOldest: _reachedOldest,
            ReturnedToNewest: _returnedToNewest,
            OrderingValid: _orderingValid,
            WindowBoundValid: _windowBoundValid,
            DeferredWorkDrained: deferredWorkDrained,
            AnchorMeasurements: _anchorMeasurements,
            MaximumAnchorErrorDip: _maximumAnchorErrorDip,
            MaximumWindowItemCount: _maximumWindowItemCount,
            MaximumRealizedCardCount: _maximumRealizedCardCount,
            EdgeP50Milliseconds: Percentile(elapsed, 0.50),
            EdgeP95Milliseconds: Percentile(elapsed, 0.95),
            EdgeMaximumMilliseconds: elapsed.Length == 0 ? 0 : elapsed[^1]);
    }

    internal static async Task MoveToEdgeAsync(
        ScrollViewer scrollViewer,
        double rowUnitHeight,
        StagePageDirection direction)
    {
        if (direction is StagePageDirection.TowardOlder)
        {
            var bottom = scrollViewer.ScrollableHeight;
            if (bottom > 0 && Math.Abs(scrollViewer.VerticalOffset - bottom) < 0.01)
            {
                scrollViewer.ScrollToVerticalOffset(
                    Math.Max(0, bottom - Math.Max(rowUnitHeight, 1)));
                await scrollViewer.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Background);
            }

            scrollViewer.ScrollToEnd();
        }
        else
        {
            if (scrollViewer.VerticalOffset < 0.01 && scrollViewer.ScrollableHeight > 0)
            {
                scrollViewer.ScrollToVerticalOffset(
                    Math.Min(scrollViewer.ScrollableHeight, Math.Max(rowUnitHeight, 1)));
                await scrollViewer.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Background);
            }

            scrollViewer.ScrollToTop();
        }

        await scrollViewer.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Background);
    }

    internal static async Task WaitForViewportReadyAsync(
        StageView view,
        StageViewModel viewModel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < SettleTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!viewModel.IsLoading && !view.HasActiveEdgePrefetch &&
                !view.HasPendingViewportMutationWork)
            {
                return;
            }

            await Dispatcher.Yield(DispatcherPriority.Normal);
            await Task.Delay(1, cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException(
            "Stage did not settle its rolling-window viewport mutation.");
    }

    internal static async Task WaitForConditionAsync(
        Func<bool> condition,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < SettleTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(10, cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException(failureMessage);
    }

    internal static bool IsStrictlyDescending(
        IReadOnlyList<StageItemViewModel> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            var previous = items[index - 1];
            var current = items[index];
            if (previous.SortOrder < current.SortOrder ||
                (previous.SortOrder == current.SortOrder &&
                 string.CompareOrdinal(
                     previous.Id.ToString("D"),
                     current.Id.ToString("D")) <= 0))
            {
                return false;
            }
        }

        return true;
    }

    internal static int CountRealizedCards(StageView view) =>
        FindVisualDescendants<Border>(view).Count(static border =>
            border.Name == "StageItemCard" && border.IsLoaded);

    internal static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var candidate in FindVisualDescendants<T>(root))
        {
            return candidate;
        }

        return null;
    }

    internal static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
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

    internal static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = Math.Max(0, (int)Math.Ceiling(sorted.Count * percentile) - 1);
        return sorted[index];
    }

    private readonly record struct WindowSignature(
        int Count,
        Guid First,
        Guid Last)
    {
        internal static WindowSignature Capture(StageViewModel viewModel) => new(
            viewModel.Items.Count,
            viewModel.Items[0].Id,
            viewModel.Items[^1].Id);
    }

    private sealed class AnchorObserver : IDisposable
    {
        private readonly StageView _view;
        private readonly StageViewModel _viewModel;
        private readonly ScrollViewer _scrollViewer;
        private PendingAnchor? _pending;
        private TaskCompletionSource<long>? _viewportMutationCompletion;
        private long _viewportMutationVersion;
        private long _stepStartedTimestamp;
        private double? _projectionChangingMilliseconds;
        private double? _itemsChangedMilliseconds;
        private double? _rowsChangedMilliseconds;
        private double? _viewportSettledMilliseconds;

        internal AnchorObserver(
            StageView view,
            StageViewModel viewModel,
            ScrollViewer scrollViewer)
        {
            _view = view;
            _viewModel = viewModel;
            _scrollViewer = scrollViewer;
            _viewModel.WindowProjectionChanging += OnProjectionChanging;
            _viewModel.Items.CollectionChanged += OnItemsChanged;
            _view.RowsProjectionPublished += OnRowsPublished;
            _view.ViewportMutationSettled += OnViewportMutationSettled;
        }

        internal long ViewportMutationVersion => _viewportMutationVersion;

        public void Dispose()
        {
            _viewModel.WindowProjectionChanging -= OnProjectionChanging;
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _view.RowsProjectionPublished -= OnRowsPublished;
            _view.ViewportMutationSettled -= OnViewportMutationSettled;
            _viewportMutationCompletion?.TrySetCanceled();
            _viewportMutationCompletion = null;
        }

        internal void BeginStepTiming()
        {
            if (_stepStartedTimestamp != 0)
            {
                throw new InvalidOperationException("A viewport timing step is already active.");
            }

            _projectionChangingMilliseconds = null;
            _itemsChangedMilliseconds = null;
            _rowsChangedMilliseconds = null;
            _viewportSettledMilliseconds = null;
            _stepStartedTimestamp = Stopwatch.GetTimestamp();
        }

        internal EdgeTiming EndStepTiming()
        {
            if (_stepStartedTimestamp == 0 ||
                _projectionChangingMilliseconds is null ||
                _itemsChangedMilliseconds is null ||
                _rowsChangedMilliseconds is null ||
                _viewportSettledMilliseconds is null)
            {
                throw new InvalidOperationException(
                    "The viewport timing step did not observe every mutation milestone.");
            }

            var timing = new EdgeTiming(
                _projectionChangingMilliseconds.Value,
                _itemsChangedMilliseconds.Value,
                _rowsChangedMilliseconds.Value,
                _viewportSettledMilliseconds.Value);
            _stepStartedTimestamp = 0;
            return timing;
        }

        internal async Task WaitForViewportMutationAsync(
            long observedVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_viewportMutationVersion != observedVersion)
            {
                return;
            }

            var completion = new TaskCompletionSource<long>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_viewportMutationCompletion is not null)
            {
                throw new InvalidOperationException(
                    "Only one viewport mutation wait may be active.");
            }

            _viewportMutationCompletion = completion;
            if (_viewportMutationVersion != observedVersion)
            {
                _viewportMutationCompletion = null;
                return;
            }

            try
            {
                _ = await completion.Task
                    .WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                if (ReferenceEquals(_viewportMutationCompletion, completion))
                {
                    _viewportMutationCompletion = null;
                }
            }
        }

        internal double? ConsumeAnchorErrorDip()
        {
            if (_pending is not { } anchor)
            {
                return null;
            }

            _pending = null;
            var current = VisibleCardPositions(_view, _scrollViewer)
                .FirstOrDefault(position => position.ItemId == anchor.ItemId);
            return current == default
                ? null
                : Math.Abs(current.Top - anchor.Top);
        }

        private void OnProjectionChanging(StageRollingWindowChange change)
        {
            _projectionChangingMilliseconds ??= ElapsedStepMilliseconds();
            if (change.IsReset)
            {
                _pending = null;
                return;
            }

            var excluded = change.EvictedItems
                .Select(static item => item.Id)
                .ToHashSet();
            var position = VisibleCardPositions(_view, _scrollViewer)
                .Where(candidate => !excluded.Contains(candidate.ItemId))
                .OrderBy(static candidate => candidate.Top)
                .ThenBy(static candidate => candidate.Left)
                .FirstOrDefault();
            _pending = position == default
                ? null
                : new PendingAnchor(position.ItemId, position.Top);
        }

        private void OnItemsChanged(
            object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
            _itemsChangedMilliseconds ??= ElapsedStepMilliseconds();

        private void OnRowsPublished() =>
            _rowsChangedMilliseconds ??= ElapsedStepMilliseconds();

        private void OnViewportMutationSettled()
        {
            _viewportSettledMilliseconds ??= ElapsedStepMilliseconds();
            _viewportMutationVersion = checked(_viewportMutationVersion + 1);
            _viewportMutationCompletion?.TrySetResult(_viewportMutationVersion);
        }

        private double ElapsedStepMilliseconds() => _stepStartedTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_stepStartedTimestamp).TotalMilliseconds;

        private static IEnumerable<VisibleCardPosition> VisibleCardPositions(
            StageView view,
            ScrollViewer scrollViewer)
        {
            foreach (var card in FindVisualDescendants<Border>(view))
            {
                if (card.Name != "StageItemCard" || !card.IsLoaded ||
                    card.DataContext is not StageItemViewModel item ||
                    card.ActualWidth <= 0 || card.ActualHeight <= 0)
                {
                    continue;
                }

                Rect bounds;
                try
                {
                    bounds = card
                        .TransformToAncestor(scrollViewer)
                        .TransformBounds(new Rect(card.RenderSize));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (bounds.Bottom > 0 && bounds.Top < scrollViewer.ViewportHeight)
                {
                    yield return new VisibleCardPosition(item.Id, bounds.Top, bounds.Left);
                }
            }
        }

        private readonly record struct PendingAnchor(Guid ItemId, double Top);

        private readonly record struct VisibleCardPosition(
            Guid ItemId,
            double Top,
            double Left);

        internal readonly record struct EdgeTiming(
            double ProjectionChangingMilliseconds,
            double ItemsChangedMilliseconds,
            double RowsChangedMilliseconds,
            double ViewportSettledMilliseconds);
    }

    internal sealed class FrameIntervalSampler
    {
        private readonly List<long> _timestamps = [];
        private bool _started;

        internal void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            CompositionTarget.Rendering += OnRendering;
        }

        internal void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        internal FrameIntervalSummary Summarize()
        {
            var intervals = _timestamps
                .Zip(_timestamps.Skip(1), static (left, right) =>
                    (right - left) * 1000.0 / Stopwatch.Frequency)
                .OrderBy(static value => value)
                .ToArray();
            return new FrameIntervalSummary(
                intervals.Length,
                Percentile(intervals, 0.50),
                Percentile(intervals, 0.95),
                intervals.Length == 0 ? 0 : intervals[^1]);
        }

        private void OnRendering(object? sender, EventArgs e) =>
            _timestamps.Add(Stopwatch.GetTimestamp());
    }
}
