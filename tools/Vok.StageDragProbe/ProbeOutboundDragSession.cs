using System.Windows;
using System.Windows.Threading;

namespace Vok.StageDragProbe;

internal interface IProbeSourcePayload : IDisposable
{
    DataObject Data { get; }
    ProbeSourcePayloadIdentity Identity { get; }
    ProbeSourcePayloadResources Resources { get; }
}

internal interface IProbeSourceLease
{
    int? Remaining { get; }
    void Release();
}

// A has no executable implementation of this seam. Only tests provide fake invokers.
internal interface IProbeSourceInvoker
{
    DragDropEffects Invoke(DataObject data, DragDropEffects allowedEffects, ProbeSourceCallbacks callbacks);
}

internal enum ProbeSourceDragDecision { KeepDefault, Cancel }
internal enum ProbeSourceCancelReason { Requested, Escape, CaptureLost, EvidenceFailure }
internal enum ProbeSourceStopReason { Close, Dispose, HostFailure }
internal enum ProbeSourcePhase { Idle, Preparing, Ready, Starting, Invoking, TerminalClaimed, Cleaning, Archiving, Stopped, Faulted }

internal sealed class ProbeSourceCallbacks(
    Action<DragDropEffects> feedback, Func<bool, DragDropKeyStates, ProbeSourceDragDecision> query)
{
    internal void GiveFeedback(DragDropEffects effect) => feedback(effect);
    internal ProbeSourceDragDecision QueryContinueDrag(bool escapePressed, DragDropKeyStates keys) =>
        query(escapePressed, keys);
}

// Admission covers construction, nested invocation, all cleanup, and asynchronous archiving.
// A cleared drag-resource count NEVER releases the admission slot on its own.
internal sealed class ProbeOutboundDragSession : IDisposable
{
    private sealed class LeaseSlot(string name)
    {
        internal readonly string Name = name;
        internal IProbeSourceLease? Lease;
        internal bool Acquired;
        internal bool Attempted;
        internal bool Succeeded;
        internal int? Remaining;
        internal ProbeSourceError? Error;
    }

    private sealed class Round(ProbeSourceIdentity identity, ProbeSourceStamp started)
    {
        internal readonly ProbeSourceIdentity Identity = identity;
        internal readonly ProbeSourceStamp Started = started;
        internal readonly TaskCompletionSource<ProbeSourceCompletion> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<ProbeSourceError> Errors = [];
        internal readonly LeaseSlot Capture = new("Capture");
        internal readonly LeaseSlot Callbacks = new("Callbacks");
        internal IProbeSourceRecorder? Recorder;
        internal IProbeSourcePayload? Payload;
        internal ProbeSourcePayloadIdentity? PayloadIdentity;
        internal ProbeSourcePayloadResources Resources = ProbeSourcePayloadResources.Empty;
        internal bool PayloadAcquired;
        internal bool PayloadAttempted;
        internal bool PayloadReleased;
        internal ProbeSourceError? PayloadError;
        internal bool CancelRequested;
        internal string? CancelReason;
        internal string? CancelPhase;
        internal ProbeSourceStamp? CancelAt;
        internal long RepeatedCancels;
        internal bool EscapeObserved;
        internal bool ReleasingCapture;
        internal bool Invoked;
        internal ProbeSourceStamp? EnteredAt;
        internal ProbeSourceStamp? ReturnedAt;
        internal int? RawEffect;
        internal ProbeSourceError? SourceException;
        internal ProbeSourceStamp? FirstFeedback;
        internal ProbeSourceStamp? FirstQuery;
        internal ProbeSourceStamp? StartObserved;
        internal int StartCount;
        internal long FeedbackCount;
        internal long QueryCount;
        internal int SuppressedErrors;
        internal Task? ArchiveTask;
    }

    private readonly Dispatcher _dispatcher;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly TimeProvider _clock;
    private readonly string _revision;
    private readonly string _runId;
    private readonly Guid _instance = Guid.NewGuid();
    private long _generation;
    private Round? _round;
    private ProbeSourceStopReason? _stop;
    private bool _hostFailure;
    private Task _completion = Task.CompletedTask;

    internal ProbeOutboundDragSession(string sourceRevision, TimeProvider? clock = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The source manager must be created on its owning STA.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _revision = sourceRevision;
        _runId = Guid.NewGuid().ToString("N");
        _clock = clock ?? TimeProvider.System;
    }

    internal ProbeSourcePhase Phase { get; private set; }
    internal ProbeSourceTicket? ActiveTicket => _round?.Identity.Ticket;
    internal ProbeSourceCompletion? LastCompletion { get; private set; }
    internal Task Completion => _completion;
    internal long IgnoredCallbackCount { get; private set; }
    internal bool DisposeRequested => _stop == ProbeSourceStopReason.Dispose;
    internal bool IsFullyDisposed => _stop is not null && _round is null &&
        (LastCompletion is null || (LastCompletion.WriterStopped && LastCompletion.Report.CleanupSucceeded &&
            LastCompletion.Report.Evidence is { StreamClosed: true }));

    internal async Task<ProbeSourceTicket?> TryPrepareAsync(
        Func<ProbeSourceIdentity, IProbeSourceRecorder> recorderFactory, Func<IProbeSourcePayload> payloadFactory)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(recorderFactory);
        ArgumentNullException.ThrowIfNull(payloadFactory);
        if (Phase != ProbeSourcePhase.Idle || _stop is not null) return null;
        Phase = ProbeSourcePhase.Preparing; // Before ANY factory can pump/reenter this STA.
        var ticket = new ProbeSourceTicket(_instance, checked(++_generation), Guid.NewGuid());
        var round = new Round(new(_runId, ticket, _revision, Environment.ProcessId, _ownerThreadId), Now());
        _round = round;
        _completion = round.Completion.Task;
        ProbeSourceError? preparationError = null;
        try
        {
            round.Recorder = recorderFactory(round.Identity) ?? throw new InvalidOperationException("Missing source recorder.");
            await round.Recorder.Ready.ConfigureAwait(false);
        }
        catch (Exception error) { preparationError = ProbeSourceError.From("Preparation", error); }
        // STA ownership cannot depend on an ambient SynchronizationContext being installed.
        await _dispatcher.InvokeAsync(() => CompletePreparation(round, payloadFactory, preparationError));
        return ticket;
    }

    private void CompletePreparation(Round round, Func<IProbeSourcePayload> payloadFactory, ProbeSourceError? preparationError)
    {
        VerifyAccess();
        if (_round != round || Phase != ProbeSourcePhase.Preparing)
            throw new InvalidOperationException("A stale preparation cannot change source ownership.");
        if (preparationError is not null) AddError(round, preparationError);
        try
        {
            Emit(round, "PreparationStarted");
            if (!round.CancelRequested && round.Errors.Count == 0)
            {
                round.Payload = payloadFactory() ?? throw new InvalidOperationException("Missing source payload.");
                round.PayloadAcquired = true; // Transfer first, before any getters that can fail/reenter.
                round.Resources = ProbeSourcePayloadResources.Unknown;
                round.PayloadIdentity = round.Payload.Identity;
                round.Resources = round.Payload.Resources;
                Emit(round, "PayloadReady");
            }
        }
        catch (Exception error) { AddError(round, ProbeSourceError.From("Preparation", error)); }

        var evidenceHealthy = CheckEvidence(round);
        if (round.CancelRequested || round.Errors.Count != 0 || !evidenceHealthy) Finish(round);
        else Phase = ProbeSourcePhase.Ready;
    }

    // Synchronous: the first await is strictly in the archive AFTER this call has returned/thrown.
    internal bool TryInvokePrepared(ProbeSourceTicket ticket, IProbeSourceInvoker invoker,
        Func<IProbeSourceLease>? acquireCapture = null,
        Func<ProbeSourceCallbacks, IProbeSourceLease>? subscribe = null)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(invoker);
        if (!Matches(ticket, out var round) || Phase != ProbeSourcePhase.Ready) return false;
        Phase = ProbeSourcePhase.Starting;
        var callbacks = new ProbeSourceCallbacks(effect => Feedback(ticket, effect), (escape, keys) => Query(ticket, escape, keys));
        try
        {
            if (acquireCapture is not null) Acquire(round.Capture, acquireCapture());
            if (CanStart(round) && subscribe is not null) Acquire(round.Callbacks, subscribe(callbacks));
            if (CanStart(round))
            {
                round.ReleasingCapture = true;
                try { Release(round, round.Capture); }
                finally { round.ReleasingCapture = false; }
            }
            if (!CanStart(round)) return true;
            var data = round.Payload!.Data;
            Emit(round, "InvocationDispatchPrepared");
            if (!CanStart(round)) return true;

            Phase = ProbeSourcePhase.Invoking;
            round.Invoked = true;
            round.EnteredAt = Now();
            // No external seam between this dispatch claim and the borrowed synchronous call.
            try { round.RawEffect = (int)invoker.Invoke(data, DragDropEffects.Copy, callbacks); }
            catch (Exception error) { round.SourceException = ProbeSourceError.From("Invocation", error); }
            finally { round.ReturnedAt = Now(); }
        }
        catch (Exception error) { AddError(round, ProbeSourceError.From("Starting", error)); }
        finally { Finish(round); }
        return true;
    }

    internal void RequestCancel(ProbeSourceTicket ticket, ProbeSourceCancelReason reason)
    {
        VerifyAccess();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (!Matches(ticket, out var round) || Phase >= ProbeSourcePhase.TerminalClaimed) return;
        if (reason == ProbeSourceCancelReason.CaptureLost && round.ReleasingCapture) return;
        Cancel(round, reason.ToString());
        if (Phase == ProbeSourcePhase.Ready) Finish(round);
    }

    internal void RequestStop(ProbeSourceStopReason reason)
    {
        VerifyAccess();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        _stop ??= reason;
        _hostFailure |= reason == ProbeSourceStopReason.HostFailure;
        if (reason == ProbeSourceStopReason.Dispose) _stop = reason;
        if (_round is not { } round)
        {
            if (Phase == ProbeSourcePhase.Idle) Phase = ProbeSourcePhase.Stopped;
            return;
        }
        if (reason == ProbeSourceStopReason.HostFailure && Phase < ProbeSourcePhase.Archiving)
            AddError(round, ProbeSourceError.Failure("HostFailure", "The host requested failure shutdown."));
        if (Phase < ProbeSourcePhase.TerminalClaimed) Cancel(round, reason.ToString());
        if (Phase == ProbeSourcePhase.Ready) Finish(round);
    }

    internal bool RequestStop(ProbeSourceTicket ticket, ProbeSourceStopReason reason)
    {
        VerifyAccess();
        if (!Matches(ticket, out _)) return false;
        RequestStop(reason);
        return true;
    }

    private void Cancel(Round round, string reason)
    {
        if (round.CancelRequested) { round.RepeatedCancels = Increment(round.RepeatedCancels); return; }
        round.CancelRequested = true; // Before recorder reentry.
        round.CancelReason = reason;
        round.CancelPhase = Phase.ToString();
        round.CancelAt = Now();
        Emit(round, "CancelRequested");
    }

    private void Feedback(ProbeSourceTicket ticket, DragDropEffects effect)
    {
        VerifyAccess();
        if (!CallbackRound(ticket, out var round)) return;
        round.FeedbackCount = Increment(round.FeedbackCount);
        if (round.FirstFeedback is null)
        {
            round.FirstFeedback = Now();
            ObserveStart(round, round.FirstFeedback);
            Emit(round, "FirstGiveFeedback");
        }
        CheckEvidence(round);
        // Feedback effects are deliberately not interpreted as an invocation result.
    }

    private ProbeSourceDragDecision Query(ProbeSourceTicket ticket, bool escape, DragDropKeyStates keys)
    {
        VerifyAccess();
        if (!CallbackRound(ticket, out var round)) return ProbeSourceDragDecision.KeepDefault;
        round.QueryCount = Increment(round.QueryCount);
        if (round.FirstQuery is null)
        {
            round.FirstQuery = Now();
            ObserveStart(round, round.FirstQuery);
            Emit(round, "FirstQueryContinueDrag");
        }
        if (escape)
        {
            round.EscapeObserved = true;
            Cancel(round, ProbeSourceCancelReason.Escape.ToString());
        }
        CheckEvidence(round);
        return round.CancelRequested ? ProbeSourceDragDecision.Cancel : ProbeSourceDragDecision.KeepDefault;
    }

    private void ObserveStart(Round round, ProbeSourceStamp stamp)
    {
        if (round.StartObserved is not null) return;
        round.StartObserved = stamp;
        round.StartCount++;
        Emit(round, "StartObserved");
    }

    private bool CallbackRound(ProbeSourceTicket ticket, out Round round)
    {
        if (Matches(ticket, out round) && Phase == ProbeSourcePhase.Invoking) return true;
        IgnoredCallbackCount = Increment(IgnoredCallbackCount);
        return false;
    }

    private bool CanStart(Round round) => CheckEvidence(round) && !round.CancelRequested && round.Errors.Count == 0;

    private bool CheckEvidence(Round round)
    {
        try
        {
            if (round.Recorder?.Fault is not { } fault) return true;
            AddError(round, fault);
        }
        catch (Exception error) { AddError(round, ProbeSourceError.From("RecorderHealth", error)); }
        if (Phase < ProbeSourcePhase.TerminalClaimed) CancelWithoutLogging(round);
        return false;
    }

    private void CancelWithoutLogging(Round round)
    {
        if (round.CancelRequested) return;
        round.CancelRequested = true;
        round.CancelReason = "EvidenceFailure";
        round.CancelPhase = Phase.ToString();
        round.CancelAt = Now();
    }

    private void Emit(Round round, string name)
    {
        try
        {
            if (round.Recorder is { } recorder && recorder.TryRecord(name, Phase.ToString(), round.FeedbackCount, round.QueryCount)) return;
            AddError(round, round.Recorder?.Fault ?? ProbeSourceError.Failure("EventRecording", "Source event was not accepted."));
        }
        catch (Exception error) { AddError(round, ProbeSourceError.From("EventRecording", error)); }
        if (Phase < ProbeSourcePhase.TerminalClaimed) CancelWithoutLogging(round);
    }

    private void Finish(Round round)
    {
        if (_round != round || Phase >= ProbeSourcePhase.TerminalClaimed) return;
        Phase = ProbeSourcePhase.TerminalClaimed;
        if (round.Invoked) Emit(round, round.SourceException is null ? "InvocationReturned" : "InvocationThrew");
        Emit(round, "TerminalClaimed");
        Phase = ProbeSourcePhase.Cleaning;
        var before = Snapshot(round, active: 1, archive: 0);
        Emit(round, "CleanupAttempted");
        Release(round, round.Callbacks);
        Release(round, round.Capture);
        if (round.Payload is { } payload)
        {
            round.PayloadAttempted = true;
            try { payload.Dispose(); }
            catch (Exception error)
            {
                round.PayloadError = ProbeSourceError.From("PayloadRelease", error);
                AddError(round, round.PayloadError);
            }
            try { round.Resources = payload.Resources; }
            catch (Exception error)
            {
                round.Resources = ProbeSourcePayloadResources.Unknown;
                AddError(round, ProbeSourceError.From("PayloadObservation", error));
            }
            round.PayloadReleased = round.PayloadError is null && round.Resources.IsEmpty;
            if (!round.PayloadReleased) AddError(round, ProbeSourceError.Failure("PayloadRemaining", "Payload release was not proven complete."));
            round.Payload = null;
        }
        var cleaned = (!round.PayloadAcquired || round.PayloadReleased) &&
            (!round.Capture.Acquired || round.Capture.Succeeded) && (!round.Callbacks.Acquired || round.Callbacks.Succeeded);
        Emit(round, cleaned ? "CleanupCompleted" : "CleanupFailed");
        Emit(round, "CallbackSummary");
        CheckEvidence(round);
        Phase = ProbeSourcePhase.Archiving;
        var report = new ProbeSourceReport(round.Identity, round.PayloadIdentity, round.Started, Now(),
            round.Invoked, round.EnteredAt, round.ReturnedAt,
            !round.Invoked ? "NotInvoked" : round.SourceException is null ? "Returned" : "Threw",
            round.RawEffect, round.SourceException,
            new(round.CancelRequested, round.CancelReason, round.CancelPhase, round.CancelAt, round.RepeatedCancels, round.EscapeObserved),
            new(round.Invoked ? "Simulated" : "NotExecuted", round.FirstFeedback, round.FirstQuery,
                round.StartObserved, round.FeedbackCount, round.QueryCount, round.StartCount),
            before, Snapshot(round, active: 0, archive: 1),
            Array.AsReadOnly(new[] { ReleaseReport(round.Callbacks), ReleaseReport(round.Capture),
                new ProbeSourceRelease("Payload", round.PayloadAcquired, round.PayloadAttempted, round.PayloadReleased, round.PayloadError) }),
            cleaned, Array.AsReadOnly(round.Errors.ToArray()), round.SuppressedErrors);
        round.ArchiveTask = ArchiveAsync(round, report);
    }

    private async Task ArchiveAsync(Round round, ProbeSourceReport report)
    {
        ProbeSourceCompletion result;
        try
        {
            result = round.Recorder is { } recorder
                ? await recorder.SealAndPublishAsync(report).ConfigureAwait(false)
                : new(report, false, null, ProbeSourceError.Failure("Publication", "No recorder was acquired."), true);
        }
        catch (Exception error) { result = new(report, false, null, ProbeSourceError.From("Publication", error), false); }
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_round != round || Phase != ProbeSourcePhase.Archiving)
                    throw new InvalidOperationException("A stale archive cannot release source admission.");
                result = result with { StopReason = _stop?.ToString(), HostFailureObserved = _hostFailure };
                LastCompletion = result;
                _round = null;
                Phase = !result.Succeeded ? ProbeSourcePhase.Faulted : _stop is not null ? ProbeSourcePhase.Stopped : ProbeSourcePhase.Idle;
                round.Completion.TrySetResult(result);
            });
        }
        catch (Exception error) { round.Completion.TrySetException(error); }
    }

    private static void Acquire(LeaseSlot slot, IProbeSourceLease lease)
    {
        slot.Lease = lease ?? throw new InvalidOperationException("Missing source lease.");
        slot.Acquired = true;
        slot.Remaining = lease.Remaining;
    }

    private static ProbeSourceRelease ReleaseReport(LeaseSlot slot) => new(slot.Name, slot.Acquired, slot.Attempted, slot.Succeeded, slot.Error);

    private static void Release(Round round, LeaseSlot slot)
    {
        if (slot.Lease is not { } lease || slot.Attempted) return;
        slot.Attempted = true;
        try { lease.Release(); }
        catch (Exception error) { slot.Error = ProbeSourceError.From(slot.Name + "Release", error); AddError(round, slot.Error); }
        try { slot.Remaining = lease.Remaining; }
        catch (Exception error) { slot.Error ??= ProbeSourceError.From(slot.Name + "Observation", error); AddError(round, slot.Error); }
        slot.Succeeded = slot.Error is null && slot.Remaining == 0;
        if (!slot.Succeeded) AddError(round, ProbeSourceError.Failure(slot.Name + "Remaining", "Lease release was not proven complete."));
        slot.Lease = null;
    }

    private static ProbeSourceResourceSnapshot Snapshot(Round round, int active, int archive) =>
        new(round.PayloadAcquired && !round.PayloadReleased ? 1 : 0, active, 1, archive, round.Resources,
            round.Capture.Acquired ? "Simulated" : "NotApplicable", round.Capture.Acquired ? round.Capture.Remaining : null,
            round.Callbacks.Acquired ? "Simulated" : "NotApplicable", round.Callbacks.Acquired ? round.Callbacks.Remaining : null);

    private static void AddError(Round round, ProbeSourceError error)
    {
        if (round.Errors.Any(existing => existing.Stage == error.Stage)) return;
        if (round.Errors.Count < 16) round.Errors.Add(error);
        else if (round.SuppressedErrors < int.MaxValue) round.SuppressedErrors++;
    }

    private bool Matches(ProbeSourceTicket ticket, out Round round)
    {
        round = _round!;
        return round is not null && round.Identity.Ticket == ticket;
    }

    private static long Increment(long count) => count == long.MaxValue ? count : count + 1;
    private ProbeSourceStamp Now() => ProbeSourceStamp.Capture(_clock);
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("The source manager belongs to another STA.");
    }
    public void Dispose() => RequestStop(ProbeSourceStopReason.Dispose);
}
