using System.Windows;

namespace Vok.StageDragProbe;

internal sealed record ProbeSourceStamp(long Ticks, long Frequency, DateTimeOffset Utc)
{
    internal static ProbeSourceStamp Capture(TimeProvider clock) =>
        new(clock.GetTimestamp(), clock.TimestampFrequency, clock.GetUtcNow());
}

internal readonly record struct ProbeSourceTicket(Guid ManagerId, long Generation, Guid RoundId);

internal sealed record ProbeSourceIdentity(
    string RunId, ProbeSourceTicket Ticket, string SourceRevision, int ProcessId, int OwnerThreadId);

internal sealed record ProbeSourcePayloadIdentity(
    string FixtureRunId, string Profile, int OccurrenceCount,
    string SourceTreeDigest, string OrderedOccurrenceDigest);

internal sealed record ProbeSourcePayloadResources(int? DataReferences, int? PreferredStreams, int? MarkerStreams)
{
    internal static ProbeSourcePayloadResources Empty { get; } = new(0, 0, 0);
    internal static ProbeSourcePayloadResources Unknown { get; } = new(null, null, null);
    internal bool IsEmpty => DataReferences == 0 && PreferredStreams == 0 && MarkerStreams == 0;
}

internal sealed record ProbeSourceError(string Stage, string Type, string Message)
{
    internal const int MaxErrorTextChars = 1024;
    internal static ProbeSourceError From(string stage, Exception error) =>
        new(stage, Limit(error.GetType().FullName ?? error.GetType().Name), Limit(error.Message));
    internal static ProbeSourceError Failure(string stage, string message) => new(stage, "ContractFailure", Limit(message));
    private static string Limit(string value) => value.Length <= MaxErrorTextChars ? value : value[..MaxErrorTextChars];
}

internal sealed record ProbeSourceResourceSnapshot(
    int PreparedPayloadOwners, int ActiveDragSessions, int RoundAdmissionSlots, int ArchivePending,
    ProbeSourcePayloadResources Payload, string CaptureScope, int? CaptureRemaining,
    string CallbackScope, int? CallbackRemaining);

internal sealed record ProbeSourceRelease(string Resource, bool Acquired, bool Attempted, bool Succeeded,
    ProbeSourceError? Error);

internal sealed record ProbeSourceCancellation(
    bool Requested, string? Reason, string? Phase, ProbeSourceStamp? First,
    long RepeatedRequests, bool EscapeObserved);

internal sealed record ProbeSourceCallbacksReport(
    string Observation, ProbeSourceStamp? FirstGiveFeedback, ProbeSourceStamp? FirstQueryContinueDrag,
    ProbeSourceStamp? StartObserved, long GiveFeedbackCount, long QueryContinueDragCount, int StartObservedCount);

internal sealed record ProbeSourceEvidenceHealth(
    int Capacity, long Produced, long Accepted, long Written, long Dropped, long Unwritten,
    long? FirstGapSequence, int HighWater, bool DrainCompleted, bool StreamFlushed, bool StreamClosed,
    IReadOnlyList<ProbeSourceError> Errors)
{
    public bool Succeeded => Errors.Count == 0 && Dropped == 0 && Unwritten == 0 &&
        Produced == Written && DrainCompleted && StreamFlushed && StreamClosed;
}

// This immutable observation ends at SealAt. Publication success is a separate receipt.
internal sealed record ProbeSourceReport(
    ProbeSourceIdentity Identity, ProbeSourcePayloadIdentity? Payload,
    ProbeSourceStamp StartedAt, ProbeSourceStamp SealAt,
    bool InvocationEntered, ProbeSourceStamp? EnteredAt, ProbeSourceStamp? ReturnedAt,
    string InvocationOutcome, int? RawEffect, ProbeSourceError? SourceException,
    ProbeSourceCancellation Cancellation, ProbeSourceCallbacksReport Callbacks,
    ProbeSourceResourceSnapshot BeforeCleanup, ProbeSourceResourceSnapshot AfterCleanup,
    IReadOnlyList<ProbeSourceRelease> Releases, bool CleanupSucceeded,
    IReadOnlyList<ProbeSourceError> Errors, int SuppressedErrorCount,
    ProbeSourceEvidenceHealth? Evidence = null)
{
    public int ReportVersion => 1;
    public string EvidenceScope => "SourceSessionSimulation";
    public string InvocationKind => "Fake";
    public bool PhysicalOleInvoked => false;
    public long? WindowHandle => null;
    public string WindowObservation => "NotExecuted";
    public string TargetObservation => "NotExecuted";
    public string PhysicalSourceComparison => "NotExecuted";
    public string ProductClaim => "NotApplicable";
    public string RenderPump => "NotApplicable";
    public string UserVerdict => "NotCaptured";
    public int ErrorTextLimit => ProbeSourceError.MaxErrorTextChars;
    public string EffectObservation => RawEffect switch
    {
        (int)DragDropEffects.Copy => "Copy",
        (int)DragDropEffects.None => "None",
        null => "NotObserved",
        _ => "UnexpectedEffect",
    };
    public bool ContractSucceeded => CleanupSucceeded && Errors.Count == 0 && SuppressedErrorCount == 0 &&
        SourceException is null && EffectObservation != "UnexpectedEffect";
    public bool Succeeded => ContractSucceeded && Evidence is { Succeeded: true };
}

internal sealed record ProbeSourceCompletion(ProbeSourceReport Report, bool Published, string? Path,
    ProbeSourceError? PublicationError, bool WriterStopped, string? StopReason = null, bool HostFailureObserved = false)
{
    public bool Succeeded => Published && WriterStopped && PublicationError is null && !HostFailureObserved && Report.Succeeded;
}

// No mutable anonymous details, exceptions, payloads, or WPF objects cross to the writer.
internal sealed record ProbeSourceEvent(
    long Sequence, ProbeSourceIdentity Identity, ProbeSourceStamp Stamp, string Name,
    string Phase, long GiveFeedbackCount, long QueryContinueDragCount)
{
    public int EventVersion => 1;
    public string EvidenceScope => "SourceSessionSimulation";
}

internal interface IProbeSourceRecorder
{
    Task Ready { get; }
    ProbeSourceError? Fault { get; }
    bool TryRecord(string name, string phase, long giveFeedbackCount, long queryContinueDragCount);
    Task<ProbeSourceCompletion> SealAndPublishAsync(ProbeSourceReport report);
}
