using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardDiagnosticState
{
    private readonly object _gate = new();
    private bool _isRunning;
    private bool _isListenerRegistered;
    private bool _isStaThread;
    private long _signalCount;
    private long _duplicateSignalCount;
    private long _supersededPendingCount;
    private long _captureAttemptCount;
    private long _capturedSnapshotCount;
    private long _excludedSnapshotCount;
    private long _selfWriteSnapshotCount;
    private long _unsupportedSnapshotCount;
    private long _busyRetryCount;
    private long _busyExhaustedCount;
    private long _failedSnapshotCount;
    private long _droppedSnapshotCount;
    private uint _lastSignaledSequence;
    private uint _lastCompletedSequence;
    private bool _hasPendingCapture;
    private string? _lastError;

    public void Started(bool isStaThread)
    {
        lock (_gate)
        {
            _isRunning = true;
            _isStaThread = isStaThread;
        }
    }

    public void ListenerRegistered()
    {
        lock (_gate)
        {
            _isListenerRegistered = true;
        }
    }

    public void Signal(
        uint sequence,
        ClipboardSequenceNotification notification,
        ClipboardSequenceState state)
    {
        lock (_gate)
        {
            _signalCount++;
            _lastSignaledSequence = sequence;
            _duplicateSignalCount += notification.IsDuplicate ? 1 : 0;
            _supersededPendingCount += notification.ReplacedPending ? 1 : 0;
            _hasPendingCapture = state.HasActive || state.HasPending;
        }
    }

    public void CaptureAttempt()
    {
        lock (_gate)
        {
            _captureAttemptCount++;
        }
    }

    public void BusyRetry()
    {
        lock (_gate)
        {
            _busyRetryCount++;
        }
    }

    public void Completed(
        ClipboardCaptureResult result,
        ClipboardSequenceState state)
    {
        lock (_gate)
        {
            _lastCompletedSequence = result.RuntimeSequence;
            _hasPendingCapture = state.HasActive || state.HasPending;
            switch (result.Disposition)
            {
                case ClipboardCaptureDisposition.Captured:
                    _capturedSnapshotCount++;
                    break;
                case ClipboardCaptureDisposition.Excluded:
                    _excludedSnapshotCount++;
                    break;
                case ClipboardCaptureDisposition.SelfWrite:
                    _selfWriteSnapshotCount++;
                    break;
                case ClipboardCaptureDisposition.Unsupported:
                    _unsupportedSnapshotCount++;
                    break;
                case ClipboardCaptureDisposition.Busy:
                    _busyExhaustedCount++;
                    _lastError = result.Error;
                    break;
                case ClipboardCaptureDisposition.Failed:
                    _failedSnapshotCount++;
                    _lastError = result.Error;
                    break;
                case ClipboardCaptureDisposition.Empty:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public void DroppedSnapshot()
    {
        lock (_gate)
        {
            _droppedSnapshotCount++;
        }
    }

    public void Fault(Exception exception)
    {
        lock (_gate)
        {
            _failedSnapshotCount++;
            _lastError = exception.Message;
        }
    }

    public void Stopped()
    {
        lock (_gate)
        {
            _isRunning = false;
            _isListenerRegistered = false;
            _hasPendingCapture = false;
        }
    }

    public ClipboardDiagnosticSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ClipboardDiagnosticSnapshot(
                _isRunning,
                _isListenerRegistered,
                _isStaThread,
                _signalCount,
                _duplicateSignalCount,
                _supersededPendingCount,
                _captureAttemptCount,
                _capturedSnapshotCount,
                _excludedSnapshotCount,
                _selfWriteSnapshotCount,
                _unsupportedSnapshotCount,
                _busyRetryCount,
                _busyExhaustedCount,
                _failedSnapshotCount,
                _droppedSnapshotCount,
                _lastSignaledSequence,
                _lastCompletedSequence,
                _hasPendingCapture,
                _lastError);
        }
    }
}
