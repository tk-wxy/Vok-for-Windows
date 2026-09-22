using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageDragProbeRouterTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void SelfTokenIsTheOnlyForeignCallAndDoesNotActivateOrAllocateGeneration()
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            using var layout = ProbeOutboundTestLayout.Create();
            using var payload = layout.CreateData(harness.Token);
            var data = new ProbeOutboundTestData(payload.Data);
            var rejected = harness.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal(new ProbeInboundRoute(0, DragDropEffects.None, "SelfDrag"), rejected);
            Assert.Equal(("present", harness.Token.FormatName, false), Assert.Single(data.Calls));
            Assert.Empty(harness.Generations);
            Assert.Equal(0, harness.Router.ActiveGeneration);
            Assert.Equal(1, harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy).Generation);
        });
    }

    [Fact]
    public void AnotherInstanceTokenIsExternalAndHoveringDoesNotReadPayload()
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            using var layout = ProbeOutboundTestLayout.Create(ProbeFixtureProfile.Duplicate);
            using var payload = layout.CreateData(new ProbeOutboundDragToken());
            var data = new ProbeOutboundTestData(payload.Data);
            var route = harness.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal("External", route.Decision);
            Assert.Equal(DragDropEffects.Copy, route.Effect);
            Assert.Equal(("present", harness.Token.FormatName, false), data.Calls[0]);
            var hoverCallCount = data.Calls.Count;
            for (var index = 0; index < 100; index++)
                Assert.Equal(DragDropEffects.Copy, harness.Router.Over(route.Generation, DragDropEffects.Copy));
            Assert.Equal(hoverCallCount, data.Calls.Count);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get");
            Assert.Equal(DragDropEffects.Copy, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
            var report = Assert.Single(Assert.Single(harness.Recorders).Reports);
            Assert.Equal("Accepted", report.Status);
            Assert.Equal(payload.Paths, report.Paths);
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, report.PreferredDropEffectBytes);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get" && call.Format == payload.TokenFormat);
            Assert.Equal(0, harness.Router.ActiveGeneration);
            var callsAfterDrop = data.Calls.Count;
            Assert.Equal(DragDropEffects.None, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
            harness.Router.Leave(route.Generation);
            harness.Router.Cancel();
            harness.Router.Dispose();
            Assert.Equal(callsAfterDrop, data.Calls.Count);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void NonCopySourcesAreRejectedBeforeFileDropProbeOrActivation(int allowed)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var data = new ProbeOutboundTestData();
            Assert.Equal("CopyNotAllowed", harness.Router.Enter(data, (DragDropEffects)allowed).Decision);
            Assert.Single(data.Calls);
            Assert.Empty(harness.Generations);
        });
    }

    [Theory]
    [InlineData("self")]
    [InlineData("com")]
    [InlineData("invalid")]
    public void SelfOrThrowingTokenStillRetiresOldSessionOnceAndNeverTouchesFileDrop(string mode)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var old = harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy);
            var raw = new DataObject(harness.Token.FormatName, new MemoryStream([1]), false);
            var data = new ProbeOutboundTestData(raw)
            {
                BeforeCall = (_, _, _) =>
                {
                    Assert.Equal(0, harness.Router.ActiveGeneration); // Claimed before foreign inspection.
                    if (mode == "com") throw new COMException("Token probe failed");
                    if (mode == "invalid") throw new InvalidOperationException("Token probe failed");
                },
            };
            var result = harness.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal(0, result.Generation);
            Assert.Equal(DragDropEffects.None, result.Effect);
            Assert.Equal(("present", harness.Token.FormatName, false), Assert.Single(data.Calls));
            Assert.Single(harness.Generations);
            Assert.Equal("Cancelled", Assert.Single(harness.Recorders[0].Reports).Status);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
            if (mode != "self") Assert.NotNull(harness.Router.LastError);

            var nextData = new ProbeOutboundTestData();
            var next = harness.Router.Enter(nextData, DragDropEffects.Copy);
            Assert.Equal(old.Generation + 1, next.Generation);
            var count = nextData.Calls.Count;
            harness.Router.Leave(old.Generation);
            Assert.Equal(DragDropEffects.None, harness.Router.Over(old.Generation, DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, harness.Router.Drop(old.Generation, nextData, DragDropEffects.Copy));
            Assert.Equal(count, nextData.Calls.Count);
            Assert.Equal(next.Generation, harness.Router.ActiveGeneration);
            Assert.Null(harness.Receivers[1].Report);
            Assert.Equal(DragDropEffects.Copy, harness.Router.Drop(next.Generation, nextData, DragDropEffects.Copy));
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("present-throws")]
    [InlineData("formats-throws")]
    [InlineData("activation-throws")]
    public void AdmissionFailuresDoNotLeaveAnActiveSession(string mode)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var data = new ProbeOutboundTestData(mode == "missing" ? new DataObject() : null);
            if (mode == "activation-throws") harness.BeforeActivation = () => throw new IOException("activation failed");
            data.BeforeCall = (operation, format, _) =>
            {
                if ((mode == "present-throws" && operation == "present" && format == DataFormats.FileDrop) ||
                    (mode == "formats-throws" && operation == "formats"))
                    throw new COMException("Admission failed");
            };
            var result = harness.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal(0, result.Generation);
            Assert.Equal(DragDropEffects.None, result.Effect);
            Assert.Equal(0, harness.Router.ActiveGeneration);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get");
            Assert.All(harness.Recorders, recorder =>
            {
                Assert.Equal("Failed", Assert.Single(recorder.Reports).Status);
                Assert.Equal(1, recorder.DisposeCount);
            });
        });
    }

    [Theory]
    [InlineData("token", false)]
    [InlineData("token", true)]
    [InlineData("file-present", false)]
    [InlineData("formats", false)]
    [InlineData("activation", false)]
    [InlineData("activation", true)]
    public void EnterCancellationOrDisposalDuringForeignCallCannotPublishNewSession(string point, bool dispose)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var data = new ProbeOutboundTestData();
            void Cancel()
            {
                Assert.Equal("Reentrant", harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy).Decision);
                if (dispose) harness.Router.Dispose();
                else harness.Router.Cancel();
            }
            if (point == "activation") harness.AfterActivation = Cancel;
            data.BeforeCall = (operation, format, _) =>
            {
                if ((point == "token" && format == harness.Token.FormatName) ||
                    (point == "file-present" && operation == "present" && format == DataFormats.FileDrop) ||
                    (point == "formats" && operation == "formats")) Cancel();
            };
            var result = harness.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal(DragDropEffects.None, result.Effect);
            Assert.Equal("Superseded", result.Decision);
            Assert.Equal(0, harness.Router.ActiveGeneration);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get");
            Assert.All(harness.Recorders, recorder =>
            {
                Assert.NotEqual("Accepted", Assert.Single(recorder.Reports).Status);
                Assert.Equal(1, recorder.DisposeCount);
            });
            if (point is "token" or "file-present") Assert.Empty(harness.Generations);
        });
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dispose")]
    [InlineData("dispose-throws")]
    public void OldCleanupReentryOrFailureCannotActivateANewReceiver(string point)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy);
            void Reenter()
            {
                Assert.Equal(0, harness.Router.ActiveGeneration);
                Assert.Equal("Reentrant", harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy).Decision);
                harness.Router.Cancel();
                if (point == "dispose-throws") throw new IOException("cleanup failed");
            }
            if (point == "complete") harness.Recorders[0].OnComplete = Reenter;
            else harness.Recorders[0].OnDispose = Reenter;
            Assert.Equal(0, harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy).Generation);
            Assert.Single(harness.Generations);
            Assert.Single(harness.Recorders[0].Reports);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
            if (point == "dispose-throws") Assert.NotNull(harness.Router.LastError);
        });
    }

    [Theory]
    [InlineData("token", false)]
    [InlineData("file-present", false)]
    [InlineData("preferred-present", false)]
    [InlineData("preferred-get", false)]
    [InlineData("file-get", false)]
    [InlineData("file-get", true)]
    public void InFlightDropRemainsCancellableAndCannotAcceptLateForeignData(string point, bool dispose)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            using var layout = ProbeOutboundTestLayout.Create();
            using var payload = layout.CreateData(new ProbeOutboundDragToken());
            var data = new ProbeOutboundTestData(payload.Data);
            var route = harness.Router.Enter(data, DragDropEffects.Copy);
            data.BeforeCall = (operation, format, _) =>
            {
                var matched = point switch
                {
                    "token" => format == harness.Token.FormatName,
                    "file-present" => operation == "present" && format == DataFormats.FileDrop,
                    "preferred-present" => operation == "present" && format == ProbeOutboundDataObject.PreferredDropEffectFormat,
                    "preferred-get" => operation == "get" && format == ProbeOutboundDataObject.PreferredDropEffectFormat,
                    "file-get" => operation == "get" && format == DataFormats.FileDrop,
                    _ => false,
                };
                if (!matched) return;
                Assert.Equal(0, harness.Router.ActiveGeneration);
                Assert.Equal(DragDropEffects.None, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
                Assert.Equal("Reentrant", harness.Router.Enter(data, DragDropEffects.Copy).Decision);
                if (dispose) harness.Router.Dispose();
                else harness.Router.Cancel();
            };
            Assert.Equal(DragDropEffects.None, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
            var recorder = Assert.Single(harness.Recorders);
            var report = Assert.Single(recorder.Reports);
            Assert.Equal("Cancelled", report.Status);
            Assert.Empty(report.Paths);
            Assert.Equal(1, recorder.DisposeCount);
            Assert.Equal(point == "file-get" ? 1 : 0, report.FileDropReadCount);
            harness.Router.Cancel();
            Assert.Equal(1, recorder.DisposeCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropRechecksTokenAndFailsClosedOnSelfOrComFailure(bool throws)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var route = harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy);
            var data = new ProbeOutboundTestData(new DataObject(harness.Token.FormatName, new MemoryStream([1]), false));
            if (throws) data.BeforeCall = (_, _, _) => throw new COMException("Drop token unavailable");
            Assert.Equal(DragDropEffects.None, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
            Assert.Single(data.Calls);
            Assert.Equal("Cancelled", Assert.Single(harness.Recorders[0].Reports).Status);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
            if (throws) Assert.NotNull(harness.Router.LastError);
        });
    }

    [Fact]
    public void CleanupFailureAfterAcceptedObservationDoesNotReturnCopyOrRetryPublication()
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var data = new ProbeOutboundTestData();
            var route = harness.Router.Enter(data, DragDropEffects.Copy);
            harness.Recorders[0].OnDispose = () => throw new IOException("dispose failed");
            Assert.Equal(DragDropEffects.None, harness.Router.Drop(route.Generation, data, DragDropEffects.Copy));
            Assert.Equal("Accepted", Assert.Single(harness.Recorders[0].Reports).Status);
            Assert.NotNull(harness.Router.LastError);
            harness.Router.Cancel();
            harness.Router.Dispose();
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
        });
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public void ExplicitTerminationClosesAdmissionUntilOldCleanupHasFinished(string terminal)
    {
        wpf.Invoke(() =>
        {
            using var harness = new RouterHarness();
            var route = harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy);
            ProbeInboundRoute? nested = null;
            harness.Recorders[0].OnComplete = () =>
                nested = harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy);
            if (terminal == "leave") harness.Router.Leave(route.Generation);
            else if (terminal == "cancel") harness.Router.Cancel();
            else harness.Router.Dispose();
            Assert.Equal(terminal == "dispose" ? "Disposed" : "Reentrant", nested!.Value.Decision);
            Assert.Single(harness.Generations);
            Assert.Equal(0, harness.Router.ActiveGeneration);
            Assert.Equal("Cancelled", Assert.Single(harness.Recorders[0].Reports).Status);
            Assert.Equal(1, harness.Recorders[0].DisposeCount);
            if (terminal != "dispose")
                Assert.Equal(route.Generation + 1,
                    harness.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy).Generation);
        });
    }

    [Fact]
    public void CancellingOneRouterDoesNotEndAnotherInstanceSession()
    {
        wpf.Invoke(() =>
        {
            using var first = new RouterHarness();
            using var second = new RouterHarness();
            var data = new ProbeOutboundTestData();
            var firstRoute = first.Router.Enter(data, DragDropEffects.Copy);
            var secondRoute = second.Router.Enter(data, DragDropEffects.Copy);
            Assert.Equal(firstRoute.Generation, secondRoute.Generation);
            first.Router.Cancel();
            Assert.Null(second.Receivers[0].Report);
            Assert.Equal(DragDropEffects.Copy, second.Router.Drop(secondRoute.Generation, data, DragDropEffects.Copy));
            Assert.Equal("Cancelled", Assert.Single(first.Recorders[0].Reports).Status);
            Assert.Equal("Accepted", Assert.Single(second.Recorders[0].Reports).Status);
        });
    }

    [Fact]
    public void RouterCallsCannotCrossOwnerSta()
    {
        RouterHarness? harness = null;
        wpf.Invoke(() => harness = new RouterHarness());
        try
        {
            Assert.Throws<InvalidOperationException>(() => harness!.Router.Enter(new ProbeOutboundTestData(), DragDropEffects.Copy));
            Assert.Throws<InvalidOperationException>(() => harness!.Router.Cancel());
            Assert.Throws<InvalidOperationException>(() => harness!.Router.Dispose());
        }
        finally { wpf.Invoke(() => harness!.Dispose()); }
    }

    private sealed class RouterHarness : IDisposable
    {
        internal ProbeOutboundDragToken Token { get; } = new();
        internal ProbeFileDropRouter Router { get; }
        internal List<long> Generations { get; } = [];
        internal List<ProbeOutboundTestRecorder> Recorders { get; } = [];
        internal List<ProbeFileDropReceiver> Receivers { get; } = [];
        internal Action? BeforeActivation { get; set; }
        internal Action? AfterActivation { get; set; }

        internal RouterHarness() => Router = new(Token, generation =>
        {
            BeforeActivation?.Invoke();
            Generations.Add(generation);
            var recorder = new ProbeOutboundTestRecorder();
            Recorders.Add(recorder);
            var receiver = new ProbeFileDropReceiver(ProbeReceiverEffect.Copy, "test", recorder);
            Receivers.Add(receiver);
            AfterActivation?.Invoke();
            return receiver;
        });

        public void Dispose() => Router.Dispose();
    }
}
