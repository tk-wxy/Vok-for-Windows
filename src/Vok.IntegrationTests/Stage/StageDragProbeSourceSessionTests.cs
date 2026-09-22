using System.IO;
using System.Windows;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageDragProbeSourceSessionTests(WpfApplicationFixture wpf)
{
    private Task Run(Func<Task> action) => wpf.InvokeAsync(async () => { await action(); return true; });

    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "Copy")]
    [InlineData(2, "UnexpectedEffect")]
    [InlineData(3, "UnexpectedEffect")]
    [InlineData(4, "UnexpectedEffect")]
    [InlineData(unchecked((int)0x80000000), "UnexpectedEffect")]
    public Task RawEffectsArePreservedAndNoCallbackIsInvented(int effect, string observation) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test-revision");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => payload))!.Value;
        var invoker = new ProbeSourceTestInvoker((_, _) => (DragDropEffects)effect);
        Assert.True(source.TryInvokePrepared(ticket, invoker));
        Assert.Equal(ProbeSourcePhase.Archiving, source.Phase);
        Assert.Equal(1, payload.DisposeCalls);
        await source.Completion;
        var completion = source.LastCompletion!;
        Assert.Equal(effect, completion.Report.RawEffect);
        Assert.Equal(observation, completion.Report.EffectObservation);
        Assert.Null(completion.Report.Callbacks.StartObserved);
        Assert.Equal(0, completion.Report.Callbacks.StartObservedCount);
        Assert.True(completion.Report.CleanupSucceeded);
        Assert.True(completion.Report.AfterCleanup.Payload.IsEmpty);
        Assert.Equal(1, completion.Report.AfterCleanup.RoundAdmissionSlots);
        Assert.Equal(1, recorder.SealCalls);
        Assert.Equal(DragDropEffects.Copy, invoker.Allowed);
        Assert.Equal(1, invoker.Calls);
        Assert.Equal(effect is 0 or 1, completion.Succeeded);
        Assert.Equal(effect is 0 or 1 ? ProbeSourcePhase.Idle : ProbeSourcePhase.Faulted, source.Phase);
        Assert.False(completion.Report.PhysicalOleInvoked);
        Assert.Null(completion.Report.WindowHandle);
    });

    [Fact]
    public Task InvocationExceptionHasNoInventedEffectAndReleasesAfterTheCall() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => payload))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((data, _) =>
        {
            Assert.Same(payload.Data, data);
            Assert.True(payload.Preferred.CanRead);
            Assert.Equal(0, payload.DisposeCalls);
            throw new IOException("Injected source exception");
        }));
        await source.Completion;
        Assert.Null(source.LastCompletion!.Report.RawEffect);
        Assert.Equal("Threw", source.LastCompletion.Report.InvocationOutcome);
        Assert.Equal("Invocation", source.LastCompletion.Report.SourceException!.Stage);
        Assert.Equal(1, payload.DisposeCalls);
        Assert.False(payload.Marker.CanRead);
        Assert.Equal(ProbeSourcePhase.Faulted, source.Phase);
        Assert.Single(recorder.Names, name => name == "TerminalClaimed");
    });

    [Theory]
    [InlineData("feedback-first")]
    [InlineData("query-first")]
    [InlineData("feedback-only")]
    [InlineData("query-only")]
    public Task FirstCallbacksHaveOneSharedEdgeAndHotCallbacksOnlyIncrementCounters(string order) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var recorder = new ProbeSourceTestRecorder();
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => new ProbeSourceTestPayload()))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, callbacks) =>
        {
            if (order == "query-first") callbacks.QueryContinueDrag(false, DragDropKeyStates.LeftMouseButton);
            for (var i = 0; i < 100_000; i++)
            {
                if (order != "query-only") callbacks.GiveFeedback(DragDropEffects.Copy);
                if (order != "feedback-only") Assert.Equal(ProbeSourceDragDecision.KeepDefault,
                    callbacks.QueryContinueDrag(false, DragDropKeyStates.None));
            }
            return DragDropEffects.None;
        }));
        await source.Completion;
        var report = source.LastCompletion!.Report;
        Assert.Equal(1, report.Callbacks.StartObservedCount);
        Assert.Single(recorder.Names, name => name == "StartObserved");
        Assert.InRange(recorder.Names.Count, 8, 16);
        Assert.Equal(order == "query-only" ? 0 : 100_000, report.Callbacks.GiveFeedbackCount);
        Assert.Equal(order == "feedback-only" ? 0 : order == "query-first" ? 100_001 : 100_000, report.Callbacks.QueryContinueDragCount);
        Assert.Equal(0, report.RawEffect); // GiveFeedback Copy was not delivery.
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("dispose")]
    [InlineData("escape")]
    [InlineData("evidence")]
    public Task InFlightRequestsCannotReleasePayloadAndLateCopyRemainsCopy(string request) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        var callbackLease = new ProbeSourceTestLease();
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => payload))!.Value;
        var invoker = new ProbeSourceTestInvoker((data, callbacks) =>
        {
            switch (request)
            {
                case "cancel": source.RequestCancel(ticket, ProbeSourceCancelReason.Requested); break;
                case "close": source.RequestStop(ProbeSourceStopReason.Close); break;
                case "dispose": source.Dispose(); source.Dispose(); Assert.False(source.IsFullyDisposed); break;
                case "escape": callbacks.QueryContinueDrag(true, DragDropKeyStates.LeftMouseButton); break;
                case "evidence": recorder.Fault = ProbeSourceError.Failure("Writer", "Injected writer failure"); break;
            }
            Assert.Equal(ProbeSourceDragDecision.Cancel, callbacks.QueryContinueDrag(false, DragDropKeyStates.None));
            Assert.Same(data, payload.Data);
            Assert.True(payload.Preferred.CanRead);
            Assert.True(payload.Marker.CanRead);
            Assert.Equal(0, payload.DisposeCalls);
            Assert.Equal(0, callbackLease.ReleaseCalls);
            Assert.False(source.Completion.IsCompleted);
            Assert.Equal(ProbeSourcePhase.Invoking, source.Phase);
            Assert.False(source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => throw new Exception("Nested"))));
            return DragDropEffects.Copy;
        });
        source.TryInvokePrepared(ticket, invoker, subscribe: _ => callbackLease);
        await source.Completion;
        var result = source.LastCompletion!;
        Assert.Equal(1, result.Report.RawEffect);
        Assert.True(result.Report.Cancellation.Requested);
        Assert.Equal(request == "escape", result.Report.Cancellation.EscapeObserved);
        Assert.Equal(1, payload.DisposeCalls);
        Assert.Equal(1, callbackLease.ReleaseCalls);
        Assert.True(result.Report.CleanupSucceeded);
        Assert.Equal(request != "evidence", result.Succeeded);
        Assert.Equal(request is "close" or "dispose", source.IsFullyDisposed);
    });

    [Theory]
    [InlineData("factory")]
    [InlineData("identity")]
    [InlineData("ready")]
    [InlineData("capture")]
    [InlineData("subscribe")]
    public Task PreInvocationCancellationRetainsOwnershipUntilConstructionOrSetupReturns(string point) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        var callbackLease = new ProbeSourceTestLease();
        var capture = new ProbeSourceTestLease();
        void Cancel() => source.RequestCancel(source.ActiveTicket!.Value, ProbeSourceCancelReason.Requested);
        if (point == "identity") payload.OnIdentity = Cancel;
        var ticket = (await source.TryPrepareAsync(_ => recorder, () =>
        {
            if (point == "factory") { Cancel(); Assert.Equal(0, payload.DisposeCalls); }
            return payload;
        }))!.Value;
        if (point == "ready") Cancel();
        var invoker = new ProbeSourceTestInvoker((_, _) => throw new Exception("Must not invoke"));
        source.TryInvokePrepared(ticket, invoker,
            () => { if (point == "capture") Cancel(); return capture; },
            _ => { if (point == "subscribe") Cancel(); return callbackLease; });
        await source.Completion;
        Assert.Equal(0, invoker.Calls);
        Assert.Equal(1, payload.DisposeCalls);
        Assert.False(source.LastCompletion!.Report.InvocationEntered);
        Assert.Null(source.LastCompletion.Report.RawEffect);
        Assert.Equal("NotExecuted", source.LastCompletion.Report.Callbacks.Observation);
        Assert.True(source.LastCompletion.Succeeded);
        Assert.Equal(point is "capture" or "subscribe" ? 1 : 0, capture.ReleaseCalls);
        Assert.Equal(point == "subscribe" ? 1 : 0, callbackLease.ReleaseCalls);
    });

    [Fact]
    public Task IntentionalCaptureReleaseDoesNotCancelButUnexpectedCaptureLossDoes() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var ticket = (await source.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => new ProbeSourceTestPayload()))!.Value;
        var capture = new ProbeSourceTestLease { OnRelease = () => source.RequestCancel(ticket, ProbeSourceCancelReason.CaptureLost) };
        var invoker = new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy);
        source.TryInvokePrepared(ticket, invoker, () => capture);
        await source.Completion;
        Assert.Equal(1, invoker.Calls);
        Assert.False(source.LastCompletion!.Report.Cancellation.Requested);
        Assert.Equal(1, capture.ReleaseCalls);
        var next = (await source.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => new ProbeSourceTestPayload()))!.Value;
        source.RequestCancel(next, ProbeSourceCancelReason.CaptureLost);
        await source.Completion;
        Assert.False(source.LastCompletion!.Report.InvocationEntered);
    });

    [Theory]
    [InlineData("capture")]
    [InlineData("callbacks")]
    [InlineData("payload")]
    [InlineData("observation")]
    public Task CleanupFailureDoesNotSkipOtherResourcesOrAllowReentry(string failure) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        var capture = new ProbeSourceTestLease { ThrowOnRelease = failure == "capture" };
        var callbackLease = new ProbeSourceTestLease { ThrowOnRelease = failure == "callbacks" };
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => payload))!.Value;
        void Reenter()
        {
            source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
            source.Dispose();
            Assert.False(source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => throw new Exception("Nested"))));
            Assert.True(source.TryPrepareAsync(_ => throw new Exception("Must not create"), () => throw new Exception("Must not create")).IsCompletedSuccessfully);
        }
        callbackLease.OnRelease = Reenter;
        payload.OnDispose = () => { Reenter(); if (failure == "observation") payload.ThrowOnObservation = true; };
        payload.ThrowOnDispose = failure == "payload";
        try
        {
            source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy), () => capture, _ => callbackLease);
            await source.Completion;
            Assert.Equal(1, capture.ReleaseCalls);
            Assert.Equal(1, callbackLease.ReleaseCalls);
            Assert.Equal(1, payload.DisposeCalls);
            Assert.False(source.LastCompletion!.Report.CleanupSucceeded);
            Assert.False(source.LastCompletion.Succeeded);
            Assert.DoesNotContain("CleanupCompleted", recorder.Names);
            Assert.Single(recorder.Names, name => name == "TerminalClaimed");
            Assert.Equal(1, recorder.SealCalls);
            Assert.Equal(ProbeSourcePhase.Faulted, source.Phase);
            Assert.Null(await source.TryPrepareAsync(_ => throw new Exception("No retry"), () => payload));
            if (failure != "capture") Assert.Equal(1, source.LastCompletion.Report.RawEffect);
        }
        finally { payload.ForceRelease(); }
    });

    [Fact]
    public Task AdmissionStaysClosedUntilArchiveAndLateCloseOnlyChangesReceipt() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new ProbeSourceTestRecorder { ArchiveGate = gate };
        try
        {
            var ticket = (await source.TryPrepareAsync(_ => recorder, () => new ProbeSourceTestPayload()))!.Value;
            source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy));
            Assert.Equal(ProbeSourcePhase.Archiving, source.Phase);
            Assert.False(source.Completion.IsCompleted);
            Assert.Null(await source.TryPrepareAsync(_ => throw new Exception("No new recorder"), () => throw new Exception("No new payload")));
            source.RequestStop(ProbeSourceStopReason.Close);
            gate.SetResult();
            await source.Completion;
            Assert.Equal("Close", source.LastCompletion!.StopReason);
            Assert.False(source.LastCompletion.Report.Cancellation.Requested);
            Assert.Equal(1, source.LastCompletion.Report.RawEffect);
            Assert.Equal(ProbeSourcePhase.Stopped, source.Phase);
        }
        finally { gate.TrySetResult(); }
    });

    [Fact]
    public Task TicketAndCallbackIsolationIncludesDifferentManagersWithTheSameGeneration() => Run(async () =>
    {
        using var first = new ProbeOutboundDragSession("test");
        using var second = new ProbeOutboundDragSession("test");
        ProbeSourceCallbacks? oldCallbacks = null;
        var firstTicket = (await first.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => new ProbeSourceTestPayload()))!.Value;
        var secondTicket = (await second.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => new ProbeSourceTestPayload()))!.Value;
        Assert.Equal(firstTicket.Generation, secondTicket.Generation);
        Assert.False(first.TryInvokePrepared(secondTicket, new ProbeSourceTestInvoker((_, _) => throw new Exception("Foreign"))));
        first.TryInvokePrepared(firstTicket, new ProbeSourceTestInvoker((_, c) => { oldCallbacks = c; return DragDropEffects.None; }));
        await first.Completion;
        var next = (await first.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => new ProbeSourceTestPayload()))!.Value;
        Assert.False(first.RequestStop(firstTicket, ProbeSourceStopReason.Close));
        first.RequestCancel(firstTicket, ProbeSourceCancelReason.Requested);
        first.TryInvokePrepared(next, new ProbeSourceTestInvoker((_, c) =>
        {
            oldCallbacks!.GiveFeedback(DragDropEffects.Copy);
            Assert.Equal(ProbeSourceDragDecision.KeepDefault, oldCallbacks.QueryContinueDrag(true, DragDropKeyStates.None));
            Assert.Equal(ProbeSourceDragDecision.KeepDefault, c.QueryContinueDrag(false, DragDropKeyStates.None));
            return DragDropEffects.Copy;
        }));
        await first.Completion;
        Assert.Equal(2, first.IgnoredCallbackCount);
        Assert.Equal(0, first.LastCompletion!.Report.Callbacks.GiveFeedbackCount);
        Assert.False(first.LastCompletion.Report.Cancellation.Requested);
        second.RequestCancel(secondTicket, ProbeSourceCancelReason.Requested);
        await second.Completion;
    });

    [Theory]
    [InlineData("invoking")]
    [InlineData("archiving")]
    public Task HostFailureIsNotSuccessEvenWhenCopyAndCleanupHaveAlreadySucceeded(string point) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var recorder = new ProbeSourceTestRecorder();
        if (point == "archiving") recorder.OnSeal = () =>
        {
            source.RequestStop(ProbeSourceStopReason.Close);
            source.RequestStop(ProbeSourceStopReason.HostFailure);
            source.Dispose(); // A later dispose must not erase the failure.
        };
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => new ProbeSourceTestPayload()))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) =>
        {
            if (point == "invoking") source.RequestStop(ProbeSourceStopReason.HostFailure);
            return DragDropEffects.Copy;
        }));
        await source.Completion;
        Assert.Equal(1, source.LastCompletion!.Report.RawEffect);
        Assert.True(source.LastCompletion.Report.CleanupSucceeded);
        Assert.False(source.LastCompletion.Succeeded);
        Assert.Equal(ProbeSourcePhase.Faulted, source.Phase);
    });

    [Fact]
    public Task PreparingExcludesSecondFactoryAndStopWaitsForRecorderReadiness() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new ProbeSourceTestRecorder { Ready = ready.Task };
        var payloadCalls = 0;
        var prepare = source.TryPrepareAsync(_ => recorder, () => { payloadCalls++; return new ProbeSourceTestPayload(); });
        try
        {
            Assert.Equal(ProbeSourcePhase.Preparing, source.Phase);
            Assert.Null(await source.TryPrepareAsync(_ => throw new Exception("Second recorder"), () => throw new Exception("Second payload")));
            source.Dispose();
            Assert.False(source.Completion.IsCompleted);
            Assert.False(source.IsFullyDisposed);
            Assert.Equal(0, recorder.SealCalls);
        }
        finally { ready.TrySetResult(); }
        Assert.NotNull(await prepare);
        await source.Completion;
        Assert.Equal(0, payloadCalls);
        Assert.True(source.IsFullyDisposed);
        Assert.Equal(1, recorder.SealCalls);
    });

    [Theory]
    [InlineData("recorder-factory")]
    [InlineData("recorder-ready")]
    [InlineData("payload-factory")]
    [InlineData("payload-identity")]
    [InlineData("payload-observation")]
    [InlineData("payload-data")]
    [InlineData("event")]
    [InlineData("seal")]
    [InlineData("lease-observation")]
    public Task ConstructionRecordingAndPublicationFaultsCannotEscapeOwnership(string failure) => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder { ThrowOnSeal = failure == "seal", RejectEvents = failure == "event" };
        if (failure == "recorder-ready") recorder.Ready = Task.FromException(new IOException("Ready failure"));
        if (failure == "payload-identity") payload.OnIdentity = () => throw new IOException("Identity failure");
        if (failure == "payload-observation") payload.ThrowOnObservation = true;
        if (failure == "payload-data") payload.OnData = () => throw new IOException("Data failure");
        var ticket = (await source.TryPrepareAsync(_ => failure == "recorder-factory" ? throw new IOException("Recorder failure") : recorder,
            () => failure == "payload-factory" ? throw new IOException("Payload failure") : payload))!.Value;
        var lease = new ProbeSourceTestLease { ThrowOnObservation = failure == "lease-observation" };
        var invoker = new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy);
        try
        {
            source.TryInvokePrepared(ticket, invoker, subscribe: _ => lease);
            await source.Completion;
            Assert.False(source.LastCompletion!.Succeeded);
            Assert.Equal(ProbeSourcePhase.Faulted, source.Phase);
            Assert.Equal(failure is "recorder-factory" or "recorder-ready" or "payload-factory" or "event" ? 0 : 1, payload.DisposeCalls);
            Assert.Equal(failure == "seal" ? 1 : 0, invoker.Calls);
            if (failure == "payload-identity") Assert.Equal(ProbeSourcePayloadResources.Unknown, source.LastCompletion.Report.BeforeCleanup.Payload);
            if (failure == "lease-observation") Assert.Equal(1, lease.ReleaseCalls);
        }
        finally { payload.ForceRelease(); }
    });

    [Fact]
    public Task FirstEdgeAndTerminalRecordReentryDoNotDoubleComplete() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var recorder = new ProbeSourceTestRecorder();
        ProbeSourceCallbacks? callbacks = null;
        recorder.OnRecord = name =>
        {
            if (name == "StartObserved") callbacks!.QueryContinueDrag(false, DragDropKeyStates.None);
            if (name == "TerminalClaimed")
            {
                source.Dispose();
                source.RequestCancel(source.ActiveTicket!.Value, ProbeSourceCancelReason.Requested);
                Assert.Equal(0, payload.DisposeCalls);
                Assert.True(source.TryPrepareAsync(_ => throw new Exception("Reentry"), () => payload).IsCompletedSuccessfully);
            }
        };
        recorder.OnSeal = () => source.Dispose();
        var ticket = (await source.TryPrepareAsync(_ => recorder, () => payload))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, c) => { callbacks = c; c.GiveFeedback(DragDropEffects.Copy); return DragDropEffects.Copy; }));
        await source.Completion;
        Assert.True(source.LastCompletion!.Succeeded);
        Assert.Equal(1, source.LastCompletion.Report.Callbacks.StartObservedCount);
        Assert.Equal(1, source.LastCompletion.Report.Callbacks.QueryContinueDragCount);
        Assert.Equal(1, payload.DisposeCalls);
        Assert.Equal(1, recorder.SealCalls);
        Assert.Single(recorder.Names, name => name == "TerminalClaimed");
    });

    [Fact]
    public Task ForeignThreadCannotPrepareInvokeCancelOrReleaseOwnerResources() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var payload = new ProbeSourceTestPayload();
        var ticket = (await source.TryPrepareAsync(_ => new ProbeSourceTestRecorder(), () => payload))!.Value;
        await Task.Run(async () =>
        {
            Assert.Throws<InvalidOperationException>(() => new ProbeOutboundDragSession("wrong thread"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.TryPrepareAsync(_ => throw new Exception("No factory"), () => payload));
            Assert.Throws<InvalidOperationException>(() => source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy)));
            Assert.Throws<InvalidOperationException>(() => source.RequestCancel(ticket, ProbeSourceCancelReason.Requested));
            Assert.Throws<InvalidOperationException>(() => source.Dispose());
        });
        Assert.Equal(0, payload.DisposeCalls);
        Assert.Equal(ProbeSourcePhase.Ready, source.Phase);
        source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
        await source.Completion;
        Assert.True(source.LastCompletion!.Succeeded);
    });

    [Fact]
    public Task AsyncPreparationReturnsToOwnerEvenWhenCallerHasNoSynchronizationContext() => Run(async () =>
    {
        using var source = new ProbeOutboundDragSession("test");
        var owner = Environment.CurrentManagedThreadId;
        var factoryThread = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProbeSourceTicket?> prepare;
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            prepare = source.TryPrepareAsync(_ => new ProbeSourceTestRecorder { Ready = ready.Task },
                () => { factoryThread = Environment.CurrentManagedThreadId; return new ProbeSourceTestPayload(); });
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await Task.Run(() => ready.SetResult());
        var ticket = (await prepare)!.Value;
        try
        {
            Assert.Equal(owner, factoryThread);
            Assert.Equal(ProbeSourcePhase.Ready, source.Phase);
        }
        finally
        {
            source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
            await source.Completion;
        }
        Assert.True(source.LastCompletion!.Succeeded);
    });
}
