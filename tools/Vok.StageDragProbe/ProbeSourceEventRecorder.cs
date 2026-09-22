using System.Text.Json;
using System.Threading.Channels;

namespace Vok.StageDragProbe;

internal interface IProbeSourceEventSink : IDisposable
{
    void Write(ProbeSourceEvent value);
    void Flush();
}

internal sealed class ProbeSourceFileEventSink : IProbeSourceEventSink
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly FileStream _stream;

    internal ProbeSourceFileEventSink(ProbeOutputDirectory output)
    {
        _stream = new FileStream(ProbeReportWriter.ResolveOutputFile(output, "events.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    public void Write(ProbeSourceEvent value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        // Per-edge visibility for a future supervisor; all disk work stays on the writer.
        _stream.Flush(flushToDisk: true);
    }
    public void Flush() => _stream.Flush(flushToDisk: true);
    public void Dispose() => _stream.Dispose();
}

// One producer (owner STA), one owned worker. Never synchronously waits for UI or storage.
internal sealed class ProbeSourceEventRecorder : IProbeSourceRecorder
{
    internal const int DefaultCapacity = 128;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly ProbeSourceIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private readonly Channel<ProbeSourceEvent> _channel;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ProbeSourceReport> _sealedReport = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<ProbeSourceCompletion> _worker;
    private ProbeSourceError? _fault;
    private ProbeSourceError? _producerFault;
    private long _produced;
    private long _accepted;
    private long _dropped;
    private long? _firstGap;
    private int _highWater;
    private bool _sealed;

    internal ProbeSourceEventRecorder(ProbeSourceIdentity identity, ProbeOutputDirectory output,
        int capacity = DefaultCapacity, TimeProvider? clock = null,
        Func<IProbeSourceEventSink>? openSink = null, Func<ProbeSourceReport, string>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(output);
        if (identity.OwnerThreadId != _ownerThreadId) throw new InvalidOperationException("Recorder must be created on its producer thread.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _identity = identity;
        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;
        _channel = Channel.CreateBounded<ProbeSourceEvent>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true, SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false,
        });
        // This task is the recorder's lifetime; SealAndPublishAsync returns THIS task, not an early signal.
        _worker = Task.Run(() => WriteAsync(output, openSink ?? (() => new ProbeSourceFileEventSink(output)), publish));
    }

    public Task Ready => _ready.Task;
    public ProbeSourceError? Fault => Volatile.Read(ref _fault);

    public bool TryRecord(string name, string phase, long giveFeedbackCount, long queryContinueDragCount)
    {
        VerifyAccess();
        if (_sealed) return false; // No mutation of a sealed generation's evidence.
        ValidateLabel(name);
        ValidateLabel(phase);
        var sequence = checked(++_produced);
        var value = new ProbeSourceEvent(sequence, _identity, ProbeSourceStamp.Capture(_clock), name,
            phase, giveFeedbackCount, queryContinueDragCount);
        if (Fault is null && _channel.Writer.TryWrite(value))
        {
            _accepted++;
            _highWater = Math.Max(_highWater, _channel.Reader.Count); // Observed queue high-water, not a latency SLA.
            return true;
        }
        _dropped++;
        _firstGap ??= sequence;
        _producerFault ??= ProbeSourceError.Failure("EventAdmission", "Source event rejected: queue full or writer unhealthy.");
        Interlocked.CompareExchange(ref _fault, _producerFault, null);
        return false;
    }

    public Task<ProbeSourceCompletion> SealAndPublishAsync(ProbeSourceReport report)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(report);
        if (report.Identity != _identity) throw new InvalidOperationException("A recorder cannot archive another round.");
        if (_sealed) return _worker;
        _sealed = true;
        var frozen = report with
        {
            Errors = Array.AsReadOnly(report.Errors.ToArray()),
            Releases = Array.AsReadOnly(report.Releases.ToArray()),
            Evidence = null,
        };
        _sealedReport.SetResult(frozen);
        _channel.Writer.TryComplete(); // Out-of-band seal: it cannot be blocked by a full queue.
        return _worker;
    }

    private async Task<ProbeSourceCompletion> WriteAsync(ProbeOutputDirectory output,
        Func<IProbeSourceEventSink> openSink, Func<ProbeSourceReport, string>? publish)
    {
        IProbeSourceEventSink? sink = null;
        ProbeTerminalReportWriter? terminal = null;
        var errors = new List<ProbeSourceError>(4);
        var written = 0L;
        long? firstWriteGap = null;
        var writeHealthy = true;
        var drained = false;
        var flushed = false;
        var closed = false;
        void Fail(string stage, Exception exception)
        {
            var error = ProbeSourceError.From(stage, exception);
            errors.Add(error); // Only the fixed Open/Write/Drain/Flush/Close stages occur once each.
            Interlocked.CompareExchange(ref _fault, error, null);
        }

        try
        {
            terminal = new ProbeTerminalReportWriter(output);
            sink = openSink() ?? throw new InvalidOperationException("Missing source event sink.");
            _ready.TrySetResult();
        }
        catch (Exception error)
        {
            Fail("EventOpen", error);
            writeHealthy = false;
            _ready.TrySetException(error);
        }

        try
        {
            await foreach (var value in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!writeHealthy) { firstWriteGap ??= value.Sequence; continue; }
                try { sink!.Write(value); written++; }
                catch (Exception error)
                {
                    firstWriteGap ??= value.Sequence;
                    writeHealthy = false; // Never append a new JSON object to a potentially partial line.
                    Fail("EventWrite", error);
                }
            }
            drained = true;
        }
        catch (Exception error) { Fail("EventDrain", error); }
        finally
        {
            if (sink is not null)
            {
                try { sink.Flush(); flushed = true; }
                catch (Exception error) { Fail("EventFlush", error); }
                try { sink.Dispose(); closed = true; }
                catch (Exception error) { Fail("EventClose", error); }
            }
        }

        var draft = await _sealedReport.Task.ConfigureAwait(false);
        if (_producerFault is { } producerFault) errors.Add(producerFault);
        var firstGap = _firstGap is null ? firstWriteGap : firstWriteGap is null ? _firstGap : Math.Min(_firstGap.Value, firstWriteGap.Value);
        var health = new ProbeSourceEvidenceHealth(_capacity, _produced, _accepted, written, _dropped,
            _accepted - written, firstGap, _highWater, drained, flushed, closed, Array.AsReadOnly(errors.ToArray()));
        var report = draft with { Evidence = health };
        try
        {
            string path;
            if (publish is not null) path = publish(report);
            else
            {
                terminal ??= new ProbeTerminalReportWriter(output);
                terminal.Complete(!report.Succeeded, report);
                path = ProbeReportWriter.ResolveOutputFile(output, report.Succeeded ? "report.json" : "failure.json");
            }
            return new(report, true, path, null, true);
        }
        catch (Exception error) { return new(report, false, null, ProbeSourceError.From("Publication", error), true); }
    }

    private static void ValidateLabel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value), "Source event labels must be bounded.");
    }
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Source recorder production belongs to another thread.");
    }
}
