namespace Vok.Core.Clipboard;

public sealed record ClipboardDiagnosticSnapshot(
    bool IsRunning,
    bool IsListenerRegistered,
    bool IsStaThread,
    long SignalCount,
    long DuplicateSignalCount,
    long SupersededPendingCount,
    long CaptureAttemptCount,
    long CapturedSnapshotCount,
    long ExcludedSnapshotCount,
    long SelfWriteSnapshotCount,
    long UnsupportedSnapshotCount,
    long BusyRetryCount,
    long BusyExhaustedCount,
    long FailedSnapshotCount,
    long DroppedSnapshotCount,
    uint LastSignaledSequence,
    uint LastCompletedSequence,
    bool HasPendingCapture,
    string? LastError);
