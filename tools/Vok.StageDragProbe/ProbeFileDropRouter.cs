using System.Windows;

namespace Vok.StageDragProbe;

internal readonly record struct ProbeInboundRoute(long Generation, DragDropEffects Effect, string Decision);

// Prototype only: the callback stands for target activation/interaction cancellation/
// lease creation/feedback. None of those effects may occur before admission succeeds.
internal sealed class ProbeFileDropRouter : IDisposable
{
    private sealed record Session(long Generation, ProbeFileDropReceiver Receiver);
    private readonly ProbeOutboundDragToken _token;
    private readonly Func<long, ProbeFileDropReceiver> _activate;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private Session? _active;
    private Session? _completing;
    private long _nextGeneration;
    private long _epoch;
    private bool _busy;
    private bool _disposed;

    internal ProbeFileDropRouter(ProbeOutboundDragToken token, Func<long, ProbeFileDropReceiver> activate)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The inbound router must be created on its owning STA.");
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
    }

    internal long ActiveGeneration => _active?.Generation ?? 0;
    internal ProbeReceiverError? LastError { get; private set; }

    internal ProbeInboundRoute Enter(IDataObject data, DragDropEffects allowed)
    {
        VerifyAccess();
        if (_disposed || _busy) return Rejected(_disposed ? "Disposed" : "Reentrant");
        _busy = true;
        LastError = null;
        var epoch = ++_epoch;
        var old = Claim(); // Invalidate the old id before any foreign call/cleanup.
        try
        {
            ArgumentNullException.ThrowIfNull(data);
            var self = data.GetDataPresent(_token.FormatName, autoConvert: false);
            var retired = old;
            old = null; // Own cleanup once, before invoking code which may reenter.
            if (!Release(retired)) return Rejected("CleanupFailed");
            if (!IsCurrent(epoch)) return Rejected("Superseded");
            if (self) return Rejected("SelfDrag");
            if ((allowed & DragDropEffects.Copy) == 0) return Rejected("CopyNotAllowed");
            var present = data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
            if (!IsCurrent(epoch)) return Rejected("Superseded");
            if (!present) return Rejected("NoFileDrop");

            var generation = ++_nextGeneration;
            var receiver = _activate(generation) ?? throw new InvalidOperationException("Missing receiver.");
            var session = new Session(generation, receiver);
            if (!IsCurrent(epoch))
            {
                Release(session);
                return Rejected("Superseded");
            }

            _active = session;
            var effect = receiver.Enter(data, allowed);
            if (!IsCurrent(epoch) || _active != session) return Rejected("Superseded");
            if (receiver.Report is not null)
            {
                Release(Claim());
                return Rejected("ReceiverTerminal");
            }
            return new(generation, effect, "External");
        }
        catch (Exception exception)
        {
            LastError = ProbeReceiverError.From(exception);
            Release(Claim());
            return Rejected("AdmissionFailed");
        }
        finally
        {
            Release(old);
            _busy = false;
        }
    }

    internal DragDropEffects Over(long generation, DragDropEffects allowed)
    {
        VerifyAccess();
        if (_disposed || _busy || _active is not { } session || session.Generation != generation)
            return DragDropEffects.None;
        _busy = true;
        try
        {
            var effect = session.Receiver.Over(allowed);
            if (_active != session) return DragDropEffects.None;
            if (session.Receiver.Report is null) return effect;
            Release(Claim());
            return DragDropEffects.None;
        }
        finally { _busy = false; }
    }

    internal DragDropEffects Drop(long generation, IDataObject data, DragDropEffects allowed)
    {
        VerifyAccess();
        if (_disposed || _busy || _active is not { } session || session.Generation != generation)
            return DragDropEffects.None;
        _busy = true;
        var epoch = ++_epoch;
        _completing = Claim(); // Drop admission is closed; cancellation still owns the in-flight receiver.
        var effect = DragDropEffects.None;
        try
        {
            ArgumentNullException.ThrowIfNull(data);
            var self = data.GetDataPresent(_token.FormatName, autoConvert: false);
            if (IsCurrent(epoch) && !self) effect = session.Receiver.Drop(data, allowed);
            if (!IsCurrent(epoch)) effect = DragDropEffects.None;
        }
        catch (Exception exception) { LastError = ProbeReceiverError.From(exception); }
        finally
        {
            if (_completing == session)
            {
                _completing = null;
                if (!Release(session)) effect = DragDropEffects.None;
            }
            _busy = false;
        }
        return effect;
    }

    internal void Leave(long generation)
    {
        VerifyAccess();
        if (_disposed || _active?.Generation != generation) return;
        Cancel();
    }

    internal void Cancel()
    {
        VerifyAccess();
        var wasBusy = _busy;
        _busy = true; // Explicit Leave/Cancel also excludes admission throughout foreign cleanup.
        try
        {
            ++_epoch;
            var active = Claim();
            var completing = _completing;
            _completing = null;
            Release(active);
            Release(completing);
        }
        finally { _busy = wasBusy; }
    }

    private Session? Claim()
    {
        var session = _active;
        _active = null;
        return session;
    }

    private bool Release(Session? session)
    {
        if (session is null) return true;
        try
        {
            try { session.Receiver.Leave(); }
            finally { session.Receiver.Dispose(); }
            if (session.Receiver.EvidenceError is not { } error) return true;
            LastError = error;
        }
        catch (Exception exception) { LastError = ProbeReceiverError.From(exception); }
        return false;
    }

    private bool IsCurrent(long epoch) => !_disposed && _epoch == epoch;
    private static ProbeInboundRoute Rejected(string reason) => new(0, DragDropEffects.None, reason);
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("The inbound router belongs to another STA.");
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }
}
