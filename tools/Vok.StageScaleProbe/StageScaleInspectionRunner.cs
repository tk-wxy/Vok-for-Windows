using System.Collections.Specialized;
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

internal sealed class StageScaleInspectionRunner(
    Application application,
    ProbeOptions options,
    ProbeMarker marker)
{
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);

    internal async Task<InspectionProbeReport> RunAsync(
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var databaseBefore = StageScaleData.AuditDatabase(
            options.DatabasePath,
            marker.StageDigest);
        var sourceHashBefore = StageScaleData.ComputeFileSha256(options.SourcePath);
        var (executablePath, executableHash) = StageScaleData.GetExecutableIdentity();
        var frameSampler = new StageScaleRunner.FrameIntervalSampler();
        await using var resourceSampler = new ResourceSampler();
        resourceSampler.SetPhase("inspect-view-model-initialize");

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
                $"The inspection Stage load failed: error={viewModel.HasError}; " +
                $"total={viewModel.TotalCount}; expected={options.ItemCount}.");
        }

        var view = new StageView
        {
            DataContext = viewModel,
        };
        var window = new Window
        {
            Title = $"Vok Stage Physical Inspection — preparing {options.ItemCount:N0}",
            Width = options.WindowWidth,
            Height = options.WindowHeight,
            WindowState = WindowState.Maximized,
            Content = view,
            ShowActivated = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.SetResourceReference(
            Control.BackgroundProperty,
            "ApplicationBackgroundBrush");

        var closedCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnWindowClosed(object? sender, EventArgs eventArgs) =>
            closedCompletion.TrySetResult(true);
        window.Closed += OnWindowClosed;

        frameSampler.Start();
        resourceSampler.SetPhase("inspect-first-viewport");
        var firstViewportStopwatch = Stopwatch.StartNew();
        window.Show();
        ScrollViewer? scrollViewer = null;
        InspectionObserver? observer = null;
        DisplaySnapshot? display = null;
        var deferredWorkDrained = false;
        var exitReason = "WindowClosed";
        try
        {
            await StageScaleRunner.WaitForConditionAsync(
                () =>
                {
                    window.UpdateLayout();
                    scrollViewer = StageScaleRunner
                        .FindVisualDescendant<ScrollViewer>(view);
                    return scrollViewer is not null &&
                        view.CardLayoutProfile is not null &&
                        scrollViewer.ViewportHeight > 0;
                },
                "The interactive Stage ScrollViewer did not finish layout.",
                cancellationToken).ConfigureAwait(true);
            await StageScaleRunner.WaitForViewportReadyAsync(
                    view,
                    viewModel,
                    cancellationToken)
                .ConfigureAwait(true);
            firstViewportStopwatch.Stop();
            display = DisplayMetrics.Capture(window, scrollViewer!, view);
            observer = new InspectionObserver(
                view,
                viewModel,
                scrollViewer!,
                resourceSampler,
                sample =>
                {
                    var manualCount = observer?.Samples.Count(static candidate =>
                        candidate.InteractionPhase == "Manual") ?? 0;
                    window.Title =
                        $"Vok Stage Physical Inspection — {options.ItemCount:N0} items — " +
                        $"manual edges {manualCount} — close to finish";
                    Console.WriteLine(
                        $"inspect edge {sample.Step}: {sample.InteractionPhase} " +
                        $"{sample.Direction}; anchor={sample.AnchorErrorDip?.ToString("F3", CultureInfo.InvariantCulture) ?? "missing"} DIP; " +
                        $"window={sample.WindowItemCount}/{sample.WindowMaximumItemCount}");
                });
            observer.ObserveCurrentWindow();
            resourceSampler.CaptureCheckpoint("inspect-first-viewport-settled");

            observer.SetInteractionPhase("Warmup");
            for (var step = 0; step < options.WarmupEdgeSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!viewModel.CanLoadOlder)
                {
                    break;
                }

                var observedVersion = observer.CompletionVersion;
                var observedSamples = observer.Samples.Count;
                await StageScaleRunner.MoveToEdgeAsync(
                        scrollViewer!,
                        view.CardRowUnitHeight,
                        StagePageDirection.TowardOlder)
                    .ConfigureAwait(true);
                await observer.WaitForCompletionAsync(
                        observedVersion,
                        TransitionTimeout,
                        cancellationToken)
                    .ConfigureAwait(true);
                await StageScaleRunner.WaitForViewportReadyAsync(
                        view,
                        viewModel,
                        cancellationToken)
                    .ConfigureAwait(true);
                if (observer.Samples.Count == observedSamples)
                {
                    throw new InvalidOperationException(
                        "An inspection warmup edge completed without a viewport transition.");
                }
            }

            observer.SetInteractionPhase("Manual");
            resourceSampler.CaptureCheckpoint(
                "inspect-manual-ready",
                observer.Samples.Count);
            if (options.CloseAfterWarmup)
            {
                exitReason = "AutomationCloseAfterWarmup";
                window.Close();
            }
            else
            {
                window.Title =
                    $"Vok Stage Physical Inspection — {options.ItemCount:N0} items — " +
                    "scroll both directions; close to finish";
                window.ShowInTaskbar = true;
                _ = window.Activate();
                view.Focus();
                Console.WriteLine(
                    "Interactive inspection is ready. Scroll in both directions, " +
                    "then close the window to finalize the report.");
            }

            _ = await closedCompletion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            resourceSampler.SetPhase("inspect-closing", observer.Samples.Count);
            await StageScaleRunner.WaitForConditionAsync(
                () => !view.HasActiveEdgePrefetch &&
                    !view.HasPendingDeferredWork &&
                    view.OutstandingAvailabilityOperationCount == 0,
                "Stage work did not drain after the inspection window closed.",
                cancellationToken).ConfigureAwait(true);
            deferredWorkDrained = true;
            observer.CompletePendingAsIncomplete();
            resourceSampler.CaptureCheckpoint(
                "inspect-closed-recovery",
                observer.Samples.Count);
        }
        finally
        {
            observer?.Dispose();
            frameSampler.Stop();
            window.Closed -= OnWindowClosed;
            if (window.IsLoaded)
            {
                window.Close();
            }
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
                "The isolated database or probe-owned source changed during inspection.");
        }

        var completedObserver = observer ??
            throw new InvalidOperationException("Inspection telemetry was not initialized.");
        var completedDisplay = display ??
            throw new InvalidOperationException("Inspection display metrics are missing.");
        var manualSamples = completedObserver.Samples
            .Where(static sample => sample.InteractionPhase == "Manual")
            .ToArray();
        var exact4K100 = completedDisplay.ScreenPixelWidth == 3840 &&
            completedDisplay.ScreenPixelHeight == 2160 &&
            completedDisplay.WindowDpi == 96;
        var objectiveChecksPassed =
            completedObserver.Samples.Count > 0 &&
            completedObserver.Samples.All(static sample => sample.MilestonesComplete) &&
            completedObserver.AnchorMeasurements == completedObserver.Samples.Count &&
            completedObserver.MaximumAnchorErrorDip <= 1.0 &&
            completedObserver.OrderingValid &&
            completedObserver.WindowBoundValid &&
            deferredWorkDrained &&
            !completedObserver.OverlappingPrefetchObserved &&
            completedObserver.IncompletePrefetchCount == 0 &&
            manualSamples.Any(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardOlder)) &&
            manualSamples.Any(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardNewer));
        var inspection = new InspectionSummary(
            WarmupRequested: options.WarmupEdgeSteps,
            WarmupCompleted: completedObserver.Samples.Count(static sample =>
                sample.InteractionPhase == "Warmup"),
            ManualTransitions: manualSamples.Length,
            ManualOlderTransitions: manualSamples.Count(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardOlder)),
            ManualNewerTransitions: manualSamples.Count(static sample =>
                sample.Direction == nameof(StagePageDirection.TowardNewer)),
            VisitedOccurrenceCount: completedObserver.VisitedOccurrenceCount,
            OrderingValid: completedObserver.OrderingValid,
            WindowBoundValid: completedObserver.WindowBoundValid,
            DeferredWorkDrained: deferredWorkDrained,
            OverlappingPrefetchObserved: completedObserver.OverlappingPrefetchObserved,
            IncompletePrefetchCount: completedObserver.IncompletePrefetchCount,
            AnchorMeasurements: completedObserver.AnchorMeasurements,
            MaximumAnchorErrorDip: completedObserver.MaximumAnchorErrorDip,
            MaximumWindowItemCount: completedObserver.MaximumWindowItemCount,
            MaximumRealizedCardCount: completedObserver.MaximumRealizedCardCount,
            ObjectiveChecksPassed: objectiveChecksPassed,
            Exact4K100Environment: exact4K100,
            PhysicalGateEligible: objectiveChecksPassed && exact4K100,
            UserVerdict: "NotCaptured");

        return new InspectionProbeReport(
            ReportVersion: 1,
            Status: "Recorded",
            Mode: "Inspect",
            StartedUtc: started.ToString("O", CultureInfo.InvariantCulture),
            CompletedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SourceRevision: options.SourceRevision,
            ExecutablePath: executablePath,
            ExecutableSha256: executableHash,
            Root: options.Root,
            RequestedItemCount: options.ItemCount,
            ExitReason: exitReason,
            ViewModelInitializeMilliseconds: initializeStopwatch.Elapsed.TotalMilliseconds,
            FirstSettledViewportMilliseconds: firstViewportStopwatch.Elapsed.TotalMilliseconds,
            Display: completedDisplay,
            DatabaseBefore: databaseBefore,
            DatabaseAfter: databaseAfter,
            SourceSha256Before: sourceHashBefore,
            SourceSha256After: sourceHashAfter,
            Inspection: inspection,
            Frames: frameSampler.Summarize(),
            Resources: ResourceSampler.Summarize(resourceSamples),
            Transitions: completedObserver.Samples,
            ResourceSamples: resourceSamples);
    }

    private sealed class InspectionObserver : IDisposable
    {
        private readonly StageView _view;
        private readonly StageViewModel _viewModel;
        private readonly ScrollViewer _scrollViewer;
        private readonly ResourceSampler _resourceSampler;
        private readonly Action<InspectionTransitionSample> _onSample;
        private readonly HashSet<Guid> _visited = [];
        private PendingTransition? _active;
        private DispatcherOperation? _pendingFinalization;
        private TaskCompletionSource<long>? _completion;
        private long _completionVersion;
        private long _nextGeneration;
        private string _interactionPhase = "Manual";
        private bool _disposed;

        internal InspectionObserver(
            StageView view,
            StageViewModel viewModel,
            ScrollViewer scrollViewer,
            ResourceSampler resourceSampler,
            Action<InspectionTransitionSample> onSample)
        {
            _view = view;
            _viewModel = viewModel;
            _scrollViewer = scrollViewer;
            _resourceSampler = resourceSampler;
            _onSample = onSample;
            _view.EdgePrefetchStarted += OnEdgePrefetchStarted;
            _view.EdgePrefetchCompleted += OnEdgePrefetchCompleted;
            _viewModel.WindowProjectionChanging += OnProjectionChanging;
            _viewModel.Items.CollectionChanged += OnItemsChanged;
            _view.RowsProjectionPublished += OnRowsPublished;
            _view.ViewportMutationSettled += OnViewportMutationSettled;
        }

        internal List<InspectionTransitionSample> Samples { get; } = [];

        internal long CompletionVersion => _completionVersion;

        internal int VisitedOccurrenceCount => _visited.Count;

        internal bool OrderingValid { get; private set; } = true;

        internal bool WindowBoundValid { get; private set; } = true;

        internal bool OverlappingPrefetchObserved { get; private set; }

        internal int IncompletePrefetchCount { get; private set; }

        internal int AnchorMeasurements { get; private set; }

        internal double MaximumAnchorErrorDip { get; private set; }

        internal int MaximumWindowItemCount { get; private set; }

        internal int MaximumRealizedCardCount { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view.EdgePrefetchStarted -= OnEdgePrefetchStarted;
            _view.EdgePrefetchCompleted -= OnEdgePrefetchCompleted;
            _viewModel.WindowProjectionChanging -= OnProjectionChanging;
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _view.RowsProjectionPublished -= OnRowsPublished;
            _view.ViewportMutationSettled -= OnViewportMutationSettled;
            CancelPendingFinalization();
            _completion?.TrySetCanceled();
            _completion = null;
        }

        internal void SetInteractionPhase(string phase)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(phase);
            _interactionPhase = phase;
        }

        internal void ObserveCurrentWindow()
        {
            var items = _viewModel.Items;
            foreach (var item in items)
            {
                _visited.Add(item.Id);
            }

            OrderingValid &= StageScaleRunner.IsStrictlyDescending(items);
            WindowBoundValid &= items.Count <= _viewModel.WindowMaximumItemCount;
            MaximumWindowItemCount = Math.Max(MaximumWindowItemCount, items.Count);
            MaximumRealizedCardCount = Math.Max(
                MaximumRealizedCardCount,
                StageScaleRunner.CountRealizedCards(_view));
        }

        internal async Task WaitForCompletionAsync(
            long observedVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_completionVersion != observedVersion)
            {
                return;
            }

            var completion = new TaskCompletionSource<long>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_completion is not null)
            {
                throw new InvalidOperationException(
                    "Only one inspection transition wait may be active.");
            }

            _completion = completion;
            if (_completionVersion != observedVersion)
            {
                _completion = null;
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
                if (ReferenceEquals(_completion, completion))
                {
                    _completion = null;
                }
            }
        }

        internal void CompletePendingAsIncomplete()
        {
            if (_active is null)
            {
                return;
            }

            CancelPendingFinalization();
            _active = null;
            IncompletePrefetchCount++;
            PublishCompletion();
        }

        private void OnEdgePrefetchStarted(StagePageDirection direction)
        {
            if (_active is not null)
            {
                OverlappingPrefetchObserved = true;
                CompletePendingAsIncomplete();
            }

            var step = Samples.Count + IncompletePrefetchCount + 1;
            _active = new PendingTransition(
                Generation: checked(++_nextGeneration),
                Step: step,
                InteractionPhase: _interactionPhase,
                Direction: direction,
                StartedTimestamp: Stopwatch.GetTimestamp());
            _resourceSampler.SetPhase(
                $"inspect-{_interactionPhase.ToLowerInvariant()}-{direction}",
                step);
        }

        private void OnEdgePrefetchCompleted(
            StagePageDirection direction,
            bool contentChanged)
        {
            if (_active is not { } active || active.Direction != direction)
            {
                return;
            }

            if (contentChanged)
            {
                active.ContentChanged = true;
                return;
            }

            CompletePendingAsIncomplete();
        }

        private void OnProjectionChanging(StageRollingWindowChange change)
        {
            if (_active is not { } active)
            {
                return;
            }

            active.ProjectionChangingMilliseconds ??= ElapsedMilliseconds(active);
            active.DirectionMatchesProjection &= change.AddedAt switch
            {
                StageRollingWindowEdge.Older =>
                    active.Direction is StagePageDirection.TowardOlder,
                StageRollingWindowEdge.Newer =>
                    active.Direction is StagePageDirection.TowardNewer,
                _ => false,
            };
            if (change.IsReset)
            {
                active.ResetObserved = true;
                active.Anchor = null;
                return;
            }

            var excluded = change.EvictedItems
                .Select(static item => item.Id)
                .ToHashSet();
            var position = VisibleCardPositions()
                .Where(candidate => !excluded.Contains(candidate.ItemId))
                .OrderBy(static candidate => candidate.Top)
                .ThenBy(static candidate => candidate.Left)
                .FirstOrDefault();
            active.Anchor = position == default
                ? null
                : new PendingAnchor(position.ItemId, position.Top);
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_active is { } active)
            {
                active.ItemsChangedMilliseconds ??= ElapsedMilliseconds(active);
            }
        }

        private void OnRowsPublished()
        {
            if (_active is { } active)
            {
                active.RowsChangedMilliseconds ??= ElapsedMilliseconds(active);
            }
        }

        private void OnViewportMutationSettled()
        {
            if (_active is not { } active || active.ViewportSignalObserved)
            {
                return;
            }

            active.ViewportSignalObserved = true;
            active.ViewportSettledMilliseconds ??= ElapsedMilliseconds(active);
            var generation = active.Generation;
            _pendingFinalization = _view.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () => FinalizeViewportMutation(generation));
        }

        private void FinalizeViewportMutation(long generation)
        {
            _pendingFinalization = null;
            if (_active is not { } active || active.Generation != generation)
            {
                return;
            }

            if (!_view.IsLoaded || !_view.IsVisible)
            {
                CompletePendingAsIncomplete();
                return;
            }

            _active = null;
            var anchorError = MeasureAnchorError(active.Anchor);
            if (anchorError is not null)
            {
                AnchorMeasurements++;
                MaximumAnchorErrorDip = Math.Max(
                    MaximumAnchorErrorDip,
                    anchorError.Value);
            }

            ObserveCurrentWindow();
            var realizedCards = StageScaleRunner.CountRealizedCards(_view);
            MaximumRealizedCardCount = Math.Max(
                MaximumRealizedCardCount,
                realizedCards);
            var items = _viewModel.Items;
            var milestonesComplete = active.ContentChanged &&
                !active.ResetObserved &&
                active.DirectionMatchesProjection &&
                active.ProjectionChangingMilliseconds is not null &&
                active.ItemsChangedMilliseconds is not null &&
                active.RowsChangedMilliseconds is not null &&
                active.ViewportSettledMilliseconds is not null;
            var sample = new InspectionTransitionSample(
                Step: active.Step,
                InteractionPhase: active.InteractionPhase,
                Direction: active.Direction.ToString(),
                ElapsedMilliseconds: ElapsedMilliseconds(active),
                ProjectionChangingMilliseconds: active.ProjectionChangingMilliseconds,
                ItemsChangedMilliseconds: active.ItemsChangedMilliseconds,
                RowsChangedMilliseconds: active.RowsChangedMilliseconds,
                ViewportSettledMilliseconds: active.ViewportSettledMilliseconds,
                MilestonesComplete: milestonesComplete,
                WindowItemCount: items.Count,
                WindowMaximumItemCount: _viewModel.WindowMaximumItemCount,
                CardRowCount: _view.CardRows.Count,
                RealizedCardCount: realizedCards,
                VerticalOffset: _scrollViewer.VerticalOffset,
                ScrollableHeight: _scrollViewer.ScrollableHeight,
                AnchorErrorDip: anchorError,
                FirstItemId: items[0].Id,
                LastItemId: items[^1].Id,
                CanLoadNewer: _viewModel.CanLoadNewer,
                CanLoadOlder: _viewModel.CanLoadOlder);
            Samples.Add(sample);
            _resourceSampler.CaptureCheckpoint("inspect-transition-settled", sample.Step);
            PublishCompletion();
            _onSample(sample);
        }

        private void CancelPendingFinalization()
        {
            if (_pendingFinalization is { Status: DispatcherOperationStatus.Pending } pending)
            {
                _ = pending.Abort();
            }

            _pendingFinalization = null;
        }

        private void PublishCompletion()
        {
            _completionVersion = checked(_completionVersion + 1);
            _completion?.TrySetResult(_completionVersion);
        }

        private double? MeasureAnchorError(PendingAnchor? anchor)
        {
            if (anchor is not { } retained)
            {
                return null;
            }

            var current = VisibleCardPositions()
                .FirstOrDefault(position => position.ItemId == retained.ItemId);
            return current == default
                ? null
                : Math.Abs(current.Top - retained.Top);
        }

        private IEnumerable<VisibleCardPosition> VisibleCardPositions()
        {
            foreach (var card in StageScaleRunner.FindVisualDescendants<Border>(_view))
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
                        .TransformToAncestor(_scrollViewer)
                        .TransformBounds(new Rect(card.RenderSize));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (bounds.Bottom > 0 && bounds.Top < _scrollViewer.ViewportHeight)
                {
                    yield return new VisibleCardPosition(
                        item.Id,
                        bounds.Top,
                        bounds.Left);
                }
            }
        }

        private static double ElapsedMilliseconds(PendingTransition transition) =>
            Stopwatch.GetElapsedTime(transition.StartedTimestamp).TotalMilliseconds;

        private sealed class PendingTransition(
            long Generation,
            int Step,
            string InteractionPhase,
            StagePageDirection Direction,
            long StartedTimestamp)
        {
            internal long Generation { get; } = Generation;

            internal int Step { get; } = Step;

            internal string InteractionPhase { get; } = InteractionPhase;

            internal StagePageDirection Direction { get; } = Direction;

            internal long StartedTimestamp { get; } = StartedTimestamp;

            internal bool ContentChanged { get; set; }

            internal bool DirectionMatchesProjection { get; set; } = true;

            internal bool ResetObserved { get; set; }

            internal bool ViewportSignalObserved { get; set; }

            internal PendingAnchor? Anchor { get; set; }

            internal double? ProjectionChangingMilliseconds { get; set; }

            internal double? ItemsChangedMilliseconds { get; set; }

            internal double? RowsChangedMilliseconds { get; set; }

            internal double? ViewportSettledMilliseconds { get; set; }
        }

        private readonly record struct PendingAnchor(Guid ItemId, double Top);

        private readonly record struct VisibleCardPosition(
            Guid ItemId,
            double Top,
            double Left);
    }
}
