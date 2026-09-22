using System.Windows;

namespace Vok.StageDragProbe;

// UI STA owned, one run/one drag generation. Never retains a foreign IDataObject.
internal sealed class ProbeFileDropReceiver : IDisposable
{
    private enum Phase { Waiting, Inspecting, Hovering, Reading, Terminal }

    private readonly IProbeReceiverRecorder _recorder;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly string _sourceRevision;
    private Phase _phase;
    private bool _disposed;
    private bool _hasFileDrop;
    private long _generation;
    private long _windowHandle;
    private bool _shown;
    private int _enterCount;
    private int _overCount;
    private int _dropCount;
    private int _fileDropReadCount;
    private int _preferredDropEffectReadCount;
    private IReadOnlyList<byte>? _preferredDropEffectBytes;
    private DragDropEffects _firstAllowedEffects;
    private DragDropEffects _lastAllowedEffects;
    private IReadOnlyList<string> _nativeFormats = Array.Empty<string>();
    private IReadOnlyList<string> _allFormats = Array.Empty<string>();

    internal ProbeFileDropReceiver(
        ProbeReceiverEffect policy,
        string sourceRevision,
        IProbeReceiverRecorder recorder,
        string? runId = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("The receiver must be created on its owning STA.");
        }

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        ArgumentNullException.ThrowIfNull(recorder);
        RunId = runId ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(RunId, "N", out _))
            throw new ArgumentException("The receiver run id must be an N-format GUID.", nameof(runId));
        Policy = policy;
        _sourceRevision = sourceRevision;
        _recorder = recorder;
        if (Record("ProcessStarted"))
        {
            Record("OutputReserved");
        }

        if (EvidenceError is not null)
        {
            Finish("Failed", "EventWriteFailed", error: EvidenceError);
        }
    }

    internal string RunId { get; }

    internal ProbeReceiverEffect Policy { get; }

    internal ProbeReceiverReport? Report { get; private set; }

    internal ProbeReceiverError? EvidenceError { get; private set; }

    internal int IgnoredEventCount { get; private set; }

    internal bool IsDisposed => _disposed;

    internal void MarkReady(long windowHandle)
    {
        VerifyAccess();
        if (_disposed || _phase == Phase.Terminal || _windowHandle != 0)
        {
            return;
        }

        if (windowHandle == 0)
        {
            Finish("Failed", "MissingWindowHandle");
            return;
        }

        _windowHandle = windowHandle;
        RecordOrFail("ReceiverReady");
    }

    internal DragDropEffects Enter(IDataObject data, DragDropEffects allowedEffects)
    {
        VerifyAccess();
        if (!CanHandleDrag("DragEnter"))
        {
            return DragDropEffects.None;
        }

        _enterCount++;
        _lastAllowedEffects = allowedEffects;
        if (_phase == Phase.Hovering)
        {
            return RecordOrFail("DragEnterRepeated") ? HoverEffect(allowedEffects) : DragDropEffects.None;
        }

        _generation = 1;
        _firstAllowedEffects = allowedEffects;
        _phase = Phase.Inspecting;
        if (!RecordOrFail("DragEnter"))
        {
            return DragDropEffects.None;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(data);
            var nativeFormats = Freeze(data.GetFormats(autoConvert: false));
            if (_phase != Phase.Inspecting) return DragDropEffects.None;
            _nativeFormats = nativeFormats;

            var allFormats = Freeze(data.GetFormats(autoConvert: true));
            if (_phase != Phase.Inspecting) return DragDropEffects.None;
            _allFormats = allFormats;

            var present = data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
            if (_phase != Phase.Inspecting) return DragDropEffects.None;
            _hasFileDrop = present;
            _phase = Phase.Hovering;
            return RecordOrFail("FormatsObserved") ? HoverEffect(allowedEffects) : DragDropEffects.None;
        }
        catch (Exception exception)
        {
            Finish("Failed", "FormatProbeFailed", error: ProbeReceiverError.From(exception));
            return DragDropEffects.None;
        }
    }

    internal void MarkShown()
    {
        VerifyAccess();
        if (_disposed || _phase == Phase.Terminal || _shown) return;
        _shown = true;
        RecordOrFail("ReceiverShown");
    }

    // No IDataObject parameter: hovering cannot accidentally fetch payload data.
    internal DragDropEffects Over(DragDropEffects allowedEffects)
    {
        VerifyAccess();
        if (!CanHandleDrag("DragOver")) return DragDropEffects.None;
        _overCount++;
        _lastAllowedEffects = allowedEffects;
        return RecordOrFail("DragOver") ? HoverEffect(allowedEffects) : DragDropEffects.None;
    }

    internal DragDropEffects Drop(IDataObject data, DragDropEffects allowedEffects)
    {
        VerifyAccess();
        if (!CanHandleDrag("Drop")) return DragDropEffects.None;
        _dropCount++;
        _lastAllowedEffects = allowedEffects;
        if (_phase != Phase.Hovering)
        {
            return Finish("Failed", "DropWithoutEnter");
        }

        // Claim before any foreign call: OLE calls can pump this STA and reenter.
        _phase = Phase.Reading;
        if (!RecordOrFail("Drop")) return DragDropEffects.None;
        if (Policy == ProbeReceiverEffect.None)
        {
            return Finish("Rejected", "PolicyNone");
        }

        if ((allowedEffects & DragDropEffects.Copy) == 0)
        {
            return Finish("Rejected", "CopyNotAllowed");
        }

        var readStage = "FileDropReadFailed";
        try
        {
            ArgumentNullException.ThrowIfNull(data);
            var present = data.GetDataPresent(DataFormats.FileDrop, autoConvert: true);
            if (_phase != Phase.Reading) return DragDropEffects.None;
            if (!present) return Finish("Rejected", "FileDropUnavailable");

            if (Policy == ProbeReceiverEffect.Throw)
            {
                throw new InvalidOperationException("Controlled receiver failure at the FileDrop read boundary.");
            }

            readStage = "PreferredDropEffectReadFailed";
            var hasPreferred = data.GetDataPresent(ProbeOutboundDataObject.PreferredDropEffectFormat, autoConvert: false);
            if (_phase != Phase.Reading) return DragDropEffects.None;
            if (hasPreferred)
            {
                _preferredDropEffectReadCount++;
                var preferred = data.GetData(ProbeOutboundDataObject.PreferredDropEffectFormat, autoConvert: false);
                if (_phase != Phase.Reading) return DragDropEffects.None;
                var bytes = preferred switch
                {
                    MemoryStream stream when stream.Length == sizeof(uint) => stream.ToArray(),
                    byte[] array when array.Length == sizeof(uint) => (byte[])array.Clone(),
                    _ => throw new InvalidDataException("Preferred DropEffect must contain exactly four raw bytes."),
                };
                if (_phase != Phase.Reading) return DragDropEffects.None;
                _preferredDropEffectBytes = Array.AsReadOnly(bytes);
            }

            readStage = "FileDropReadFailed";
            _fileDropReadCount++;
            var value = data.GetData(DataFormats.FileDrop, autoConvert: true);
            if (_phase != Phase.Reading) return DragDropEffects.None;
            if (value is not string[] { Length: > 0 } paths ||
                paths.Any(string.IsNullOrWhiteSpace))
            {
                return Finish("Failed", "InvalidFileDrop");
            }

            return Finish("Accepted", "Drop", DragDropEffects.Copy, Freeze(paths));
        }
        catch (Exception exception)
        {
            return Finish(
                "Failed",
                Policy == ProbeReceiverEffect.Throw ? "ControlledReadFailure" : readStage,
                error: ProbeReceiverError.From(exception));
        }
    }

    internal void Leave() => End("Cancelled", "DragLeave");

    internal void Cancel() => End("Cancelled", "Escape");

    internal void CloseWindow() => End("Failed", "WindowClosedBeforeDrop");

    internal void FailHost(Exception exception)
    {
        VerifyAccess();
        if (!_disposed) Finish("Failed", "HostFailure", error: ProbeReceiverError.From(exception));
    }

    // Called on the owning STA by a future process host; never denotes success.
    internal void WatchdogExpired() => End("Failed", "WatchdogExpired");

    private void End(string status, string reason)
    {
        VerifyAccess();
        if (!_disposed) Finish(status, reason);
    }

    private DragDropEffects HoverEffect(DragDropEffects allowedEffects) =>
        _phase == Phase.Hovering && _hasFileDrop && Policy != ProbeReceiverEffect.None &&
        (allowedEffects & DragDropEffects.Copy) != 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private bool CanHandleDrag(string name)
    {
        if (_disposed) return false;
        if (_phase is Phase.Inspecting or Phase.Reading or Phase.Terminal)
        {
            IgnoredEventCount++;
            Record("Ignored" + name);
            return false;
        }

        return true;
    }

    private DragDropEffects Finish(
        string status,
        string reason,
        DragDropEffects effect = DragDropEffects.None,
        IReadOnlyList<string>? paths = null,
        ProbeReceiverError? error = null)
    {
        if (_phase == Phase.Terminal || _disposed)
        {
            if (!_disposed)
            {
                IgnoredEventCount++;
                Record("IgnoredTerminal", reason);
            }

            return DragDropEffects.None;
        }

        _phase = Phase.Terminal;
        var terminalEventWritten = Record("ReceiverTerminal", reason);
        if (!terminalEventWritten || EvidenceError is not null)
        {
            status = "Failed";
            reason = "EventWriteFailed";
            effect = DragDropEffects.None;
            error ??= EvidenceError;
        }

        Report = new ProbeReceiverReport(
            1, "ReceiverObservation", RunId, _generation, _sourceRevision, Policy.ToString(),
            Environment.ProcessId, _ownerThreadId, _windowHandle, _startedUtc, DateTimeOffset.UtcNow,
            status, reason, (int)effect, (int)_firstAllowedEffects, (int)_lastAllowedEffects,
            _enterCount, _overCount, _dropCount, _fileDropReadCount,
            _nativeFormats, _allFormats, paths ?? Array.Empty<string>(), error,
            _preferredDropEffectBytes, _preferredDropEffectReadCount);
        try
        {
            _recorder.Complete(Report);
        }
        catch (Exception exception)
        {
            EvidenceError ??= ProbeReceiverError.From(exception);
            Report = Report with
            {
                Status = "Failed", Reason = "ReportWriteFailed", RawEffect = 0,
                Error = ProbeReceiverError.From(exception),
            };
            Record("ReportWriteFailed", exception.Message);
        }

        return EvidenceError is null ? (DragDropEffects)Report.RawEffect : DragDropEffects.None;
    }

    private bool RecordOrFail(string name)
    {
        if (Record(name) && EvidenceError is null) return _phase != Phase.Terminal;
        Finish("Failed", "EventWriteFailed", error: EvidenceError);
        return false;
    }

    private bool Record(string name, string? detail = null)
    {
        try
        {
            _recorder.Record(name, new
            {
                runId = RunId, generation = _generation, phase = _phase.ToString(),
                processId = Environment.ProcessId, ownerThreadId = _ownerThreadId,
                windowHandle = _windowHandle, policy = Policy.ToString(), sourceRevision = _sourceRevision,
                enterCount = _enterCount, overCount = _overCount, dropCount = _dropCount,
                fileDropReadCount = _fileDropReadCount, allowedEffects = (int)_lastAllowedEffects,
                preferredDropEffectReadCount = _preferredDropEffectReadCount,
                detail,
            });
            return true;
        }
        catch (Exception exception)
        {
            EvidenceError ??= ProbeReceiverError.From(exception);
            return false;
        }
    }

    private static IReadOnlyList<string> Freeze(string[] values) =>
        Array.AsReadOnly((string[])values.Clone());

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("The receiver may only be used by its owning STA.");
        }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        if (_phase != Phase.Terminal) Finish("Failed", "DisposedBeforeDrop");
        _disposed = true;
        try
        {
            _recorder.Dispose();
        }
        catch (Exception exception)
        {
            EvidenceError ??= ProbeReceiverError.From(exception);
        }
    }
}
