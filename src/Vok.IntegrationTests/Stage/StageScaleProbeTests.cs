using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Persistence;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageScaleProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageScaleProbeTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void InspectOptionsAreExplicitAndCannotLeakIntoAutomatedRun()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var options = ProbeOptions.Parse(
            [
                "inspect",
                "--allowed-root", allowedRoot,
                "--root", fixtureRoot,
                "--report", Path.Combine(fixtureRoot, "inspection.json"),
                "--source-revision", "test-revision",
                "--count", "256",
                "--warmup-edge-steps", "3",
                "--close-after-warmup", "true",
            ]);

            Assert.Equal(ProbeVerb.Inspect, options.Verb);
            Assert.Equal(3, options.WarmupEdgeSteps);
            Assert.True(options.CloseAfterWarmup);
            Assert.Equal(0, options.EdgeSteps);

            Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(
            [
                "run",
                "--allowed-root", allowedRoot,
                "--root", fixtureRoot,
                "--report", Path.Combine(fixtureRoot, "run.json"),
                "--source-revision", "test-revision",
                "--count", "256",
                "--warmup-edge-steps", "1",
            ]));
            Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(
            [
                "inspect",
                "--allowed-root", allowedRoot,
                "--root", fixtureRoot,
                "--report", Path.Combine(fixtureRoot, "inspection.json"),
                "--source-revision", "test-revision",
                "--count", "256",
                "--edge-steps", "1",
            ]));
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InspectionRepositoryRejectsEveryMutationWithoutChangingFixture()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-read-only-repository-test";
        try
        {
            var options = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 256,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(options, CancellationToken.None);
            var marker = StageScaleData.ValidatePreparedFixture(options);
            var databaseBefore = StageScaleData.AuditDatabase(options.DatabasePath);
            var sourceSha256Before = StageScaleData.ComputeFileSha256(options.SourcePath);

            using (var repository = new SqliteStageRepository(
                       options.DatabasePath,
                       new StageOptions(options.ItemCount)))
            {
                var readOnly = new InspectionReadOnlyStageRepository(repository);
                var page = await readOnly.LoadPageAsync(
                    null,
                    StagePageDirection.TowardOlder,
                    64,
                    CancellationToken.None);
                var item = Assert.IsType<StageItem>(page.Items.FirstOrDefault());
                var target = new StageTargetDescriptor(
                    StageItemKind.File,
                    options.SourcePath,
                    Path.GetFullPath(options.SourcePath).ToUpperInvariant(),
                    "blocked-mutation.txt");
                var reorder = new StageReorderRequest(
                    new StageItemVersion(item.Id, item.Revision),
                    null,
                    null);

                await AssertRejectedAsync(() => readOnly.AddBatchAsync([target]));
                await AssertRejectedAsync(() =>
                    readOnly.RemoveAsync(item.Id, item.Revision));
                await AssertRejectedAsync(() =>
                    readOnly.SetPinnedAsync(item.Id, item.Revision, !item.IsPinned));
                await AssertRejectedAsync(() =>
                    readOnly.RelinkAsync(item.Id, item.Revision, target));
                await AssertRejectedAsync(() => readOnly.ReorderAsync(reorder));
            }

            var databaseAfter = StageScaleData.AuditDatabase(options.DatabasePath);
            Assert.Equal(marker.StageDigest, databaseBefore.StageDigest);
            Assert.Equal(databaseBefore.StageDigest, databaseAfter.StageDigest);
            Assert.Equal(databaseBefore.StageCount, databaseAfter.StageCount);
            Assert.Equal(
                sourceSha256Before,
                StageScaleData.ComputeFileSha256(options.SourcePath));
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EventDrivenInspectionWarmupClosesAndDrainsWithoutClaimingUserPass()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-lifecycle-test";
        try
        {
            var prepareOptions = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 256,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(
                prepareOptions,
                CancellationToken.None);
            var inspectOptions = prepareOptions with
            {
                Verb = ProbeVerb.Inspect,
                ReportPath = Path.Combine(fixtureRoot, "inspection.json"),
                WarmupEdgeSteps = 1,
                CloseAfterWarmup = true,
            };
            var marker = StageScaleData.ValidatePreparedFixture(inspectOptions);

            var report = await wpf.InvokeAsync(() =>
                new StageScaleInspectionRunner(
                    wpf.Application,
                    inspectOptions,
                    marker)
                .RunAsync(CancellationToken.None));

            Assert.Equal("Recorded", report.Status);
            Assert.Equal("AutomationCloseAfterWarmup", report.ExitReason);
            Assert.Equal("NotCaptured", report.Inspection.UserVerdict);
            Assert.Equal(1, report.Inspection.WarmupCompleted);
            Assert.Equal(0, report.Inspection.ManualTransitions);
            Assert.False(report.Inspection.ObjectiveChecksPassed);
            Assert.False(report.Inspection.PhysicalGateEligible);
            Assert.True(report.Inspection.DeferredWorkDrained);
            Assert.False(report.Inspection.OverlappingPrefetchObserved);
            Assert.Equal(0, report.Inspection.IncompletePrefetchCount);
            var transition = Assert.Single(report.Transitions);
            Assert.Equal("Warmup", transition.InteractionPhase);
            Assert.Equal("TowardOlder", transition.Direction);
            Assert.True(transition.MilestonesComplete);
            Assert.NotNull(transition.AnchorErrorDip);
            Assert.InRange(transition.AnchorErrorDip!.Value, 0, 1);
            Assert.Equal(
                report.DatabaseBefore.StageDigest,
                report.DatabaseAfter.StageDigest);
            Assert.Equal(
                report.SourceSha256Before,
                report.SourceSha256After);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingAtPrefetchStartRecordsOneIncompleteAttemptAndStillDrains()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-close-race-test";
        try
        {
            var prepareOptions = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 256,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(
                prepareOptions,
                CancellationToken.None);
            var inspectOptions = prepareOptions with
            {
                Verb = ProbeVerb.Inspect,
                ReportPath = Path.Combine(fixtureRoot, "inspection.json"),
            };
            var marker = StageScaleData.ValidatePreparedFixture(inspectOptions);

            var report = await wpf.InvokeAsync(async () =>
            {
                var runTask = new StageScaleInspectionRunner(
                    wpf.Application,
                    inspectOptions,
                    marker)
                    .RunAsync(CancellationToken.None);
                Window? window = null;
                StageView? view = null;
                await StageScaleRunner.WaitForConditionAsync(
                    () =>
                    {
                        window = wpf.Application.Windows
                            .OfType<Window>()
                            .FirstOrDefault(candidate =>
                                candidate.Title.Contains(
                                    "scroll both directions",
                                    StringComparison.Ordinal));
                        view = window?.Content as StageView;
                        return view is not null;
                    },
                    "The interactive inspection window did not become ready.",
                    CancellationToken.None);
                var currentWindow = Assert.IsType<Window>(window);
                var currentView = Assert.IsType<StageView>(view);
                void CloseOnPrefetch(StagePageDirection direction)
                {
                    currentView.EdgePrefetchStarted -= CloseOnPrefetch;
                    currentWindow.Close();
                }

                currentView.EdgePrefetchStarted += CloseOnPrefetch;
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    StageScaleRunner.FindVisualDescendant<ScrollViewer>(currentView));
                scrollViewer.ScrollToEnd();
                return await runTask;
            });

            Assert.Equal("Recorded", report.Status);
            Assert.Equal("WindowClosed", report.ExitReason);
            Assert.Equal(1, report.Inspection.IncompletePrefetchCount);
            Assert.Empty(report.Transitions);
            Assert.False(report.Inspection.ObjectiveChecksPassed);
            Assert.False(report.Inspection.PhysicalGateEligible);
            Assert.True(report.Inspection.DeferredWorkDrained);
            Assert.False(report.Inspection.OverlappingPrefetchObserved);
            Assert.Equal(
                report.DatabaseBefore.StageDigest,
                report.DatabaseAfter.StageDigest);
            Assert.Equal(
                report.SourceSha256Before,
                report.SourceSha256After);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingAtViewportSignalCannotPublishALateMeasurement()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-late-measurement-test";
        try
        {
            var prepareOptions = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 256,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(
                prepareOptions,
                CancellationToken.None);
            var inspectOptions = prepareOptions with
            {
                Verb = ProbeVerb.Inspect,
                ReportPath = Path.Combine(fixtureRoot, "inspection.json"),
            };
            var marker = StageScaleData.ValidatePreparedFixture(inspectOptions);

            var report = await wpf.InvokeAsync(async () =>
            {
                var runTask = new StageScaleInspectionRunner(
                    wpf.Application,
                    inspectOptions,
                    marker)
                    .RunAsync(CancellationToken.None);
                Window? window = null;
                StageView? view = null;
                await StageScaleRunner.WaitForConditionAsync(
                    () =>
                    {
                        window = wpf.Application.Windows
                            .OfType<Window>()
                            .FirstOrDefault(candidate =>
                                candidate.Title.Contains(
                                    "scroll both directions",
                                    StringComparison.Ordinal));
                        view = window?.Content as StageView;
                        return view is not null;
                    },
                    "The interactive inspection window did not become ready.",
                    CancellationToken.None);
                var currentWindow = Assert.IsType<Window>(window);
                var currentView = Assert.IsType<StageView>(view);
                void CloseOnViewportSettled()
                {
                    currentView.ViewportMutationSettled -= CloseOnViewportSettled;
                    currentWindow.Close();
                }

                currentView.ViewportMutationSettled += CloseOnViewportSettled;
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    StageScaleRunner.FindVisualDescendant<ScrollViewer>(currentView));
                scrollViewer.ScrollToEnd();
                return await runTask;
            });

            Assert.Equal("Recorded", report.Status);
            Assert.Equal(1, report.Inspection.IncompletePrefetchCount);
            Assert.Empty(report.Transitions);
            Assert.True(report.Inspection.DeferredWorkDrained);
            Assert.False(report.Inspection.OverlappingPrefetchObserved);
            Assert.Equal(
                report.DatabaseBefore.StageDigest,
                report.DatabaseAfter.StageDigest);
            Assert.Equal(
                report.SourceSha256Before,
                report.SourceSha256After);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueuedUserScrollCannotContaminateTheSettledAnchorMeasurement()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-input-priority-test";
        try
        {
            var prepareOptions = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 512,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(
                prepareOptions,
                CancellationToken.None);
            var inspectOptions = prepareOptions with
            {
                Verb = ProbeVerb.Inspect,
                ReportPath = Path.Combine(fixtureRoot, "inspection.json"),
            };
            var marker = StageScaleData.ValidatePreparedFixture(inspectOptions);

            var report = await wpf.InvokeAsync(async () =>
            {
                var runTask = new StageScaleInspectionRunner(
                    wpf.Application,
                    inspectOptions,
                    marker)
                    .RunAsync(CancellationToken.None);
                Window? window = null;
                StageView? view = null;
                await StageScaleRunner.WaitForConditionAsync(
                    () =>
                    {
                        window = wpf.Application.Windows
                            .OfType<Window>()
                            .FirstOrDefault(candidate =>
                                candidate.Title.Contains(
                                    "scroll both directions",
                                    StringComparison.Ordinal));
                        view = window?.Content as StageView;
                        return view is not null;
                    },
                    "The interactive inspection window did not become ready.",
                    CancellationToken.None);
                var currentWindow = Assert.IsType<Window>(window);
                var currentView = Assert.IsType<StageView>(view);
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    StageScaleRunner.FindVisualDescendant<ScrollViewer>(currentView));
                var inputQueued = false;
                void QueueUserScroll()
                {
                    if (inputQueued)
                    {
                        return;
                    }

                    inputQueued = true;
                    _ = currentView.Dispatcher.BeginInvoke(
                        DispatcherPriority.Input,
                        () => scrollViewer.ScrollToVerticalOffset(
                            Math.Min(
                                scrollViewer.ScrollableHeight,
                                scrollViewer.VerticalOffset +
                                    currentView.CardRowUnitHeight)));
                }

                currentView.ViewportMutationSettled += QueueUserScroll;
                try
                {
                    scrollViewer.ScrollToEnd();
                    await StageScaleRunner.WaitForConditionAsync(
                        () => currentWindow.Title.Contains(
                            "manual edges 1",
                            StringComparison.Ordinal),
                        "The input-priority transition did not settle.",
                        CancellationToken.None);
                }
                finally
                {
                    currentView.ViewportMutationSettled -= QueueUserScroll;
                    if (currentWindow.IsLoaded)
                    {
                        currentWindow.Close();
                    }

                    if (!runTask.IsCompleted)
                    {
                        _ = await runTask;
                    }
                }

                Assert.True(inputQueued);
                return await runTask;
            });

            var transition = Assert.Single(report.Transitions);
            Assert.Equal("Manual", transition.InteractionPhase);
            Assert.True(transition.MilestonesComplete);
            Assert.Equal(0, transition.AnchorErrorDip);
            Assert.Equal(
                report.DatabaseBefore.StageDigest,
                report.DatabaseAfter.StageDigest);
            Assert.Equal(
                report.SourceSha256Before,
                report.SourceSha256After);
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BidirectionalManualPhasePassesObjectiveChecksButNotUserVerdict()
    {
        var allowedRoot = CreateTemporaryRoot();
        var fixtureRoot = Path.Combine(allowedRoot, "fixture");
        const string sourceRevision = "inspection-objective-checks-test";
        try
        {
            var prepareOptions = new ProbeOptions(
                ProbeVerb.Prepare,
                allowedRoot,
                fixtureRoot,
                Path.Combine(fixtureRoot, "prepare.json"),
                sourceRevision,
                ItemCount: 512,
                EdgeSteps: 0,
                WarmupEdgeSteps: 0,
                CloseAfterWarmup: false,
                WindowWidth: 1000,
                WindowHeight: 700);
            _ = await StageScaleData.PrepareAsync(
                prepareOptions,
                CancellationToken.None);
            var inspectOptions = prepareOptions with
            {
                Verb = ProbeVerb.Inspect,
                ReportPath = Path.Combine(fixtureRoot, "inspection.json"),
            };
            var marker = StageScaleData.ValidatePreparedFixture(inspectOptions);

            var report = await wpf.InvokeAsync(async () =>
            {
                var runTask = new StageScaleInspectionRunner(
                    wpf.Application,
                    inspectOptions,
                    marker)
                    .RunAsync(CancellationToken.None);
                Window? window = null;
                StageView? view = null;
                await StageScaleRunner.WaitForConditionAsync(
                    () =>
                    {
                        window = wpf.Application.Windows
                            .OfType<Window>()
                            .FirstOrDefault(candidate =>
                                candidate.Title.Contains(
                                    "scroll both directions",
                                    StringComparison.Ordinal));
                        view = window?.Content as StageView;
                        return view is not null;
                    },
                    "The interactive inspection window did not become ready.",
                    CancellationToken.None);
                var currentWindow = Assert.IsType<Window>(window);
                var currentView = Assert.IsType<StageView>(view);
                var scrollViewer = Assert.IsType<ScrollViewer>(
                    StageScaleRunner.FindVisualDescendant<ScrollViewer>(currentView));
                var viewModel = Assert.IsType<StageViewModel>(currentView.DataContext);
                Assert.False(viewModel.MutationsAllowed);
                Assert.NotEmpty(viewModel.Items);
                Assert.Equal(0, viewModel.BeginReorder(viewModel.Items[0]));
                var edgeEvents = new List<string>();
                void OnEdgeStarted(StagePageDirection direction) =>
                    edgeEvents.Add($"{direction}:started");
                void OnEdgeCompleted(StagePageDirection direction, bool contentChanged) =>
                    edgeEvents.Add($"{direction}:completed:{contentChanged}");
                currentView.EdgePrefetchStarted += OnEdgeStarted;
                currentView.EdgePrefetchCompleted += OnEdgeCompleted;
                try
                {
                    var completedManualEdges = 0;
                    while (!viewModel.CanLoadNewer)
                    {
                        Assert.True(viewModel.CanLoadOlder);
                        Assert.InRange(completedManualEdges, 0, 9);
                        await StageScaleRunner.MoveToEdgeAsync(
                            scrollViewer,
                            currentView.CardRowUnitHeight,
                            StagePageDirection.TowardOlder);
                        completedManualEdges++;
                        await StageScaleRunner.WaitForConditionAsync(
                            () => currentWindow.Title.Contains(
                                $"manual edges {completedManualEdges}",
                                StringComparison.Ordinal),
                            "A manual older transition did not settle.",
                            CancellationToken.None);
                        await StageScaleRunner.WaitForViewportReadyAsync(
                            currentView,
                            viewModel,
                            CancellationToken.None);
                    }

                    await StageScaleRunner.MoveToEdgeAsync(
                        scrollViewer,
                        currentView.CardRowUnitHeight,
                        StagePageDirection.TowardNewer);
                    completedManualEdges++;
                    try
                    {
                        await StageScaleRunner.WaitForConditionAsync(
                            () => currentWindow.Title.Contains(
                                $"manual edges {completedManualEdges}",
                                StringComparison.Ordinal),
                            "The manual newer transition did not settle.",
                            CancellationToken.None);
                    }
                    catch (TimeoutException exception)
                    {
                        throw new InvalidOperationException(
                            $"The manual newer transition did not settle. " +
                            $"offset={scrollViewer.VerticalOffset:F3}; " +
                            $"height={scrollViewer.ScrollableHeight:F3}; " +
                            $"canNewer={viewModel.CanLoadNewer}; " +
                            $"canOlder={viewModel.CanLoadOlder}; " +
                            $"active={currentView.HasActiveEdgePrefetch}; " +
                            $"pending={currentView.HasPendingDeferredWork}; " +
                            $"events=[{string.Join(",", edgeEvents)}]",
                            exception);
                    }

                    await StageScaleRunner.WaitForViewportReadyAsync(
                        currentView,
                        viewModel,
                        CancellationToken.None);
                    for (var reversal = 0; reversal < 3; reversal++)
                    {
                        Assert.True(viewModel.CanLoadOlder);
                        await StageScaleRunner.MoveToEdgeAsync(
                            scrollViewer,
                            currentView.CardRowUnitHeight,
                            StagePageDirection.TowardOlder);
                        completedManualEdges++;
                        await StageScaleRunner.WaitForConditionAsync(
                            () => currentWindow.Title.Contains(
                                $"manual edges {completedManualEdges}",
                                StringComparison.Ordinal),
                            "A reversal toward older items did not settle.",
                            CancellationToken.None);
                        await StageScaleRunner.WaitForViewportReadyAsync(
                            currentView,
                            viewModel,
                            CancellationToken.None);

                        Assert.True(viewModel.CanLoadNewer);
                        await StageScaleRunner.MoveToEdgeAsync(
                            scrollViewer,
                            currentView.CardRowUnitHeight,
                            StagePageDirection.TowardNewer);
                        completedManualEdges++;
                        await StageScaleRunner.WaitForConditionAsync(
                            () => currentWindow.Title.Contains(
                                $"manual edges {completedManualEdges}",
                                StringComparison.Ordinal),
                            "A reversal toward newer items did not settle.",
                            CancellationToken.None);
                        await StageScaleRunner.WaitForViewportReadyAsync(
                            currentView,
                            viewModel,
                            CancellationToken.None);
                    }
                }
                finally
                {
                    currentView.EdgePrefetchStarted -= OnEdgeStarted;
                    currentView.EdgePrefetchCompleted -= OnEdgeCompleted;
                    if (currentWindow.IsLoaded)
                    {
                        currentWindow.Close();
                    }

                    if (!runTask.IsCompleted)
                    {
                        _ = await runTask;
                    }
                }

                return await runTask;
            });

            Assert.Equal("Recorded", report.Status);
            Assert.Equal("NotCaptured", report.Inspection.UserVerdict);
            Assert.True(report.Inspection.ManualTransitions >= 2);
            Assert.True(report.Inspection.ManualOlderTransitions >= 4);
            Assert.Equal(4, report.Inspection.ManualNewerTransitions);
            Assert.Equal(
                report.Inspection.ManualOlderTransitions +
                    report.Inspection.ManualNewerTransitions,
                report.Inspection.ManualTransitions);
            Assert.True(report.Inspection.ObjectiveChecksPassed);
            Assert.Equal(
                report.Inspection.ObjectiveChecksPassed &&
                    report.Inspection.Exact4K100Environment,
                report.Inspection.PhysicalGateEligible);
            Assert.Equal(
                report.Inspection.ManualTransitions,
                report.Inspection.AnchorMeasurements);
            Assert.InRange(report.Inspection.MaximumAnchorErrorDip, 0, 1);
            Assert.Equal(
                report.DatabaseBefore.StageDigest,
                report.DatabaseAfter.StageDigest);
            Assert.Equal(
                report.SourceSha256Before,
                report.SourceSha256After);
            Assert.All(report.Transitions, transition =>
            {
                Assert.Equal("Manual", transition.InteractionPhase);
                Assert.True(transition.MilestonesComplete);
            });
        }
        finally
        {
            Directory.Delete(allowedRoot, recursive: true);
        }
    }

    private static async Task AssertRejectedAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(InspectionReadOnlyStageRepository.RejectionMessage, exception.Message);
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vok-stage-scale-inspection-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
