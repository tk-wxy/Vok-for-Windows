using System.Diagnostics;

namespace Vok.Core.Diagnostics;

/// <summary>Opt-in, instance-owned flight recorder. Never performs I/O on a producer.</summary>
public sealed class RevealTrace
{
    public const int DefaultCapacity = 2048;
    public static RevealTrace Disabled { get; } = new(enabled: false);
    private readonly Lock _gate = new();
    private readonly RevealTraceEntry[] _entries;
    private long _sequence;
    private long _nextId;
    private int _count;
    private long _lastResumeTimestamp;
    private long _lastUnlockTimestamp;

    public RevealTrace(bool enabled, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Enabled = enabled;
        _entries = enabled ? new RevealTraceEntry[capacity] : [];
    }

    public bool Enabled { get; }
    public event Action? ExportRequested;
    public long NextId() => Enabled ? Interlocked.Increment(ref _nextId) : 0;

    public void Record(
        string phase, long generation = 0, long sourceId = 0, long operationId = 0,
        Guid itemId = default, long revision = 0, long value1 = 0, long value2 = 0,
        string? detail = null, StageRevealState? stage = null, long timestamp = 0)
    {
        if (!Enabled) return;
        var observedTimestamp = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp;
        lock (_gate)
        {
            if (phase == "PowerMode" && detail == "Resume") _lastResumeTimestamp = observedTimestamp;
            if (phase == "SessionSwitch" && detail == "SessionUnlock") _lastUnlockTimestamp = observedTimestamp;
            var sequence = ++_sequence;
            _entries[(int)((sequence - 1) % _entries.Length)] = new(
                sequence, observedTimestamp, Environment.CurrentManagedThreadId,
                phase, generation, sourceId, operationId, itemId, revision,
                value1, value2, detail, stage);
            _count = Math.Min(_count + 1, _entries.Length);
        }
    }

    public RevealTraceSnapshot Capture()
    {
        lock (_gate)
        {
            var entries = new RevealTraceEntry[_count];
            var first = _sequence - _count;
            for (var index = 0; index < entries.Length; index++)
                entries[index] = _entries[(int)((first + index) % _entries.Length)];
            return new(1, Environment.ProcessId, Stopwatch.Frequency,
                _sequence - _count, entries)
            {
                LastResumeTimestamp = _lastResumeTimestamp,
                LastUnlockTimestamp = _lastUnlockTimestamp,
            };
        }
    }

    // Only called after presentation/cleanup, never from Rendering.
    public void RequestExport()
    {
        if (!Enabled || ExportRequested is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception exception)
            {
                Record("TraceExportRequestFailed", detail: exception.GetType().Name);
            }
        }
    }
}

public readonly record struct RevealTraceEntry(
    long Sequence, long Timestamp, int ThreadId, string Phase,
    long Generation, long SourceId, long OperationId, Guid ItemId, long Revision,
    long Value1, long Value2, string? Detail, StageRevealState? Stage);

public sealed record RevealTraceSnapshot(
    int Version, int ProcessId, long TimestampFrequency,
    long OverwrittenEntries, IReadOnlyList<RevealTraceEntry> Entries)
{
    public long LastResumeTimestamp { get; init; }
    public long LastUnlockTimestamp { get; init; }
}

/// <summary>Observed managed layout state; not proof of a physical display frame.</summary>
public sealed record StageRevealState(
    long ContentVersion, long LayoutVersion, int ItemCount, int RowCount,
    int ExpectedVisibleCards, int RealizedVisibleCards, ulong VisibleIdentityHash,
    double ViewportWidth, double ViewportHeight, double VerticalOffset,
    bool PendingProjection, bool PendingLayout, bool PendingScroll,
    bool IsMeasureValid, bool IsArrangeValid, bool IsEmpty, bool HasError,
    bool IsLoading, bool StaticContentReady);
