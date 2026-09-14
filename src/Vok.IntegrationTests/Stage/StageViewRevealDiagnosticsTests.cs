using System.Windows;
using System.Windows.Threading;
using Vok.Core.Diagnostics;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed partial class StageViewIntegrationTests
{
    [Fact]
    public void HiddenStagePublishesItsRowsBeforeTheFirstWindowShow()
    {
        wpf.Invoke(() =>
        {
            using var model = new StageViewModel(
                new StaticStageRepository(46),
                new NoopStageFileService());
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };

            view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

            Assert.Equal(46, view.CardRows.Sum(static row => row.Items.Count));
            Assert.False(view.IsLoaded);
        });
    }

    [Fact]
    public void RevealReadinessWaitsForStaticCardsThenStartsBoundedAvailabilityAfterCompletion()
    {
        wpf.Invoke(() =>
        {
            var files = new DeferredStageFileService();
            using var model = new StageViewModel(new StaticStageRepository(50), files);
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };
            var readyCount = 0;
            WorkspaceRevealReadinessToken? readyToken = null;
            view.RevealReady += (_, e) =>
            {
                readyCount++;
                readyToken = e.Token;
            };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
            };

            view.BeginReveal(17);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

                Assert.Equal(1, readyCount);
                Assert.True(readyToken.HasValue);
                Assert.True(view.IsRevealReady(readyToken.Value));
                Assert.Empty(files.Requests);

                view.CompleteReveal(17);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

                Assert.InRange(files.Requests.Count, 1, 4);
                Assert.InRange(view.OutstandingAvailabilityOperationCount, 1, 4);
                var completedRequest = files.Requests[0];
                completedRequest.Completion.SetResult(StageFileAvailability.Available);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                var completedItem = Assert.Single(
                    model.Items,
                    item => item.Id == completedRequest.Item.Id);
                Assert.True(completedItem.HasAvailabilityResult);

                window.Hide();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

                Assert.True(completedItem.HasAvailabilityResult);
                Assert.All(
                    files.Requests.Where(request => !request.Completion.Task.IsCompleted),
                    request => Assert.True(request.CancellationToken.IsCancellationRequested));

                var requestCountAfterHide = files.Requests.Count;
                view.BeginReveal(18);
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                view.CompleteReveal(18);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

                Assert.Equal(requestCountAfterHide, files.Requests.Count);
                Assert.InRange(view.OutstandingAvailabilityOperationCount, 1, 4);
                window.Hide();
            }
            finally
            {
                window.Close();
                foreach (var request in files.Requests)
                {
                    request.Completion.TrySetCanceled();
                }
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            }
        });
    }

    [Fact]
    public void StaticReadinessTokenIsInvalidAfterContentChanges()
    {
        wpf.Invoke(() =>
        {
            using var model = new StageViewModel(
                new StaticStageRepository(20),
                new NoopStageFileService());
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };
            WorkspaceRevealReadinessToken? readyToken = null;
            view.RevealReady += (_, e) => readyToken = e.Token;
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
            };

            view.BeginReveal(23);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.True(readyToken.HasValue);
                Assert.True(view.IsRevealReady(readyToken.Value));

                var first = model.Items[0];
                model.Items.RemoveAt(0);
                model.Items.Add(first);

                Assert.False(view.IsRevealReady(readyToken.Value));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompletedAvailabilitySurvivesHideAndRefreshesAfterTheNextPresentation()
    {
        wpf.Invoke(() =>
        {
            var files = new DeferredStageFileService();
            using var model = new StageViewModel(new StaticStageRepository(4), files);
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };
            var window = new Window
            {
                Width = 520,
                Height = 320,
                Content = view,
                ShowInTaskbar = false,
            };

            view.BeginReveal(31);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                view.CompleteReveal(31);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(4, files.Requests.Count);
                foreach (var request in files.Requests)
                {
                    request.Completion.SetResult(StageFileAvailability.Available);
                }
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.All(model.Items, item => Assert.True(item.HasAvailabilityResult));

                window.Hide();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.All(model.Items, item => Assert.True(item.HasAvailabilityResult));

                view.BeginReveal(32);
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(4, files.Requests.Count);

                view.CompleteReveal(32);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(8, files.Requests.Count);
            }
            finally
            {
                window.Close();
                foreach (var request in files.Requests)
                {
                    request.Completion.TrySetCanceled();
                }
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(46)]
    [InlineData(50)]
    public void RevealObservationSeparatesDeferredRowsFromStaticContentAndPendingFileChecks(int count)
    {
        wpf.Invoke(() =>
        {
            var trace = new RevealTrace(true);
            var files = new DeferredStageFileService();
            using var model = new StageViewModel(new StaticStageRepository(count), files);
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };
            view.ConfigureRevealDiagnostics(trace);
            StageRevealState? atLoaded = null;
            view.Loaded += (_, _) => atLoaded = view.CaptureRevealState();
            var window = new Window { Width = 520, Height = 320, Content = view, ShowInTaskbar = false };
            view.ObserveReveal("ShowBegin", 1);
            try
            {
                Assert.False(view.CaptureRevealState().StaticContentReady);
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.NotNull(atLoaded);
                Assert.False(atLoaded.StaticContentReady);
                Assert.True(atLoaded.PendingProjection);
                var stable = view.CaptureRevealState();
                Assert.True(stable.StaticContentReady, stable.ToString());
                Assert.Equal(count == 0, stable.IsEmpty);
                Assert.All(files.Requests, request => Assert.False(request.Completion.Task.IsCompleted));
                Assert.Equal(stable.ExpectedVisibleCards, stable.RealizedVisibleCards);
                if (count > 0) Assert.InRange(stable.RealizedVisibleCards, 1, count - 1);
                Assert.Contains(trace.Capture().Entries, entry => entry.Phase == "StageRowsPublished");

                if (count > 0)
                {
                    var item = model.Items[0];
                    model.Items.RemoveAt(0);
                    model.Items.Add(item);
                    // Capture synchronously, while the actual projection is still queued.
                    Assert.False(view.CaptureRevealState().StaticContentReady);
                    Assert.True(view.CaptureRevealState().PendingProjection);
                }
            }
            finally
            {
                window.Close();
                foreach (var request in files.Requests) request.Completion.TrySetCanceled();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            }
        });
    }

    [Fact]
    public void RevealTraceShowsHideRetainsRowsAndLateChecksKeepTheirOriginalGeneration()
    {
        wpf.Invoke(() =>
        {
            var trace = new RevealTrace(true);
            var files = new DeferredStageFileService();
            using var model = new StageViewModel(new StaticStageRepository(46), files);
            model.InitializeAsync().GetAwaiter().GetResult();
            var view = new StageView { DataContext = model };
            view.ConfigureRevealDiagnostics(trace);
            var window = new Window { Width = 520, Height = 320, Content = view, ShowInTaskbar = false };
            try
            {
                view.ObserveReveal("ShowBegin", 1);
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                var rows = view.CardRows.ToArray();
                var oldRequests = files.Requests.ToArray();
                window.Hide();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.Equal(rows, view.CardRows);
                Assert.All(oldRequests, request => Assert.True(request.CancellationToken.IsCancellationRequested));
                view.ObserveReveal("ShowBegin", 2);
                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                foreach (var request in oldRequests) request.Completion.SetResult(StageFileAvailability.Missing);
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
                Assert.All(model.Items, item => Assert.False(item.IsMissing));
                var entries = trace.Capture().Entries;
                Assert.Single(entries, entry => entry.Phase == "StageSourceAttached");
                Assert.DoesNotContain(entries, entry => entry.Phase == "StageUnloaded");
                Assert.Contains(entries, entry => entry.Phase == "StageAvailabilityReturned" && entry.Generation == 1);
                Assert.DoesNotContain(entries, entry => entry.Phase == "StageAvailabilityApplied" && entry.Generation == 1);
            }
            finally
            {
                window.Close();
                foreach (var request in files.Requests) request.Completion.TrySetCanceled();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
            }
        });
    }
}
