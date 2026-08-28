namespace Vok.Core.Clipboard;

public readonly record struct ClipboardSequenceNotification(
    bool IsDuplicate,
    bool ReplacedPending,
    bool ShouldScheduleCapture,
    uint Sequence);

public readonly record struct ClipboardSequenceCompletion(
    bool ShouldScheduleCapture,
    uint Sequence);

public readonly record struct ClipboardSequenceState(
    uint CommittedSequence,
    uint ActiveSequence,
    uint PendingSequence,
    bool HasActive,
    bool HasPending);

public sealed class ClipboardSequenceCoordinator
{
    private readonly object _gate = new();
    private uint _committedSequence;
    private uint _activeSequence;
    private uint _pendingSequence;
    private bool _hasActive;
    private bool _hasPending;
    private bool _initialized;

    public void Initialize(uint currentSequence)
    {
        lock (_gate)
        {
            if (_initialized)
            {
                throw new InvalidOperationException(
                    "The clipboard sequence coordinator is already initialized.");
            }

            _committedSequence = currentSequence;
            _initialized = true;
        }
    }

    public ClipboardSequenceNotification Notify(uint sequence)
    {
        lock (_gate)
        {
            ThrowIfNotInitialized();
            if ((_hasActive && _activeSequence == sequence) ||
                (_hasPending && _pendingSequence == sequence) ||
                (!_hasActive && !_hasPending && _committedSequence == sequence))
            {
                return new ClipboardSequenceNotification(
                    IsDuplicate: true,
                    ReplacedPending: false,
                    ShouldScheduleCapture: false,
                    sequence);
            }

            if (!_hasActive)
            {
                _activeSequence = sequence;
                _hasActive = true;
                return new ClipboardSequenceNotification(
                    IsDuplicate: false,
                    ReplacedPending: false,
                    ShouldScheduleCapture: true,
                    sequence);
            }

            var replacedPending = _hasPending;
            _pendingSequence = sequence;
            _hasPending = true;
            return new ClipboardSequenceNotification(
                IsDuplicate: false,
                ReplacedPending: replacedPending,
                ShouldScheduleCapture: false,
                sequence);
        }
    }

    public ClipboardSequenceCompletion Complete(uint observedSequence)
    {
        lock (_gate)
        {
            ThrowIfNotInitialized();
            if (!_hasActive)
            {
                throw new InvalidOperationException(
                    "There is no active clipboard capture to complete.");
            }

            _committedSequence = observedSequence;
            _hasActive = false;
            _activeSequence = 0;

            if (!_hasPending)
            {
                return new ClipboardSequenceCompletion(false, 0);
            }

            if (_pendingSequence == observedSequence)
            {
                _hasPending = false;
                _pendingSequence = 0;
                return new ClipboardSequenceCompletion(false, 0);
            }

            var pending = _pendingSequence;
            _hasPending = false;
            _pendingSequence = 0;
            _activeSequence = pending;
            _hasActive = true;
            return new ClipboardSequenceCompletion(true, pending);
        }
    }

    public ClipboardSequenceState GetState()
    {
        lock (_gate)
        {
            ThrowIfNotInitialized();
            return new ClipboardSequenceState(
                _committedSequence,
                _activeSequence,
                _pendingSequence,
                _hasActive,
                _hasPending);
        }
    }

    private void ThrowIfNotInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "The clipboard sequence coordinator has not been initialized.");
        }
    }
}
