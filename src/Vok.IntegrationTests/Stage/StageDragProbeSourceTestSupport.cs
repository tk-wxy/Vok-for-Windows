using System.IO;
using System.Windows;
using Vok.StageDragProbe;

namespace Vok.IntegrationTests.Stage;

internal sealed class ProbeSourceTestPayload : IProbeSourcePayload
{
    private DataObject? _data = new(DataFormats.FileDrop, new[] { @"C:\probe-owned-test\fixture.txt" }, false);
    internal readonly MemoryStream Preferred = new([1, 0, 0, 0], writable: false);
    internal readonly MemoryStream Marker = new([1], writable: false);
    internal int DisposeCalls;
    internal Action? OnDispose;
    internal bool ThrowOnDispose;
    internal bool ThrowOnObservation { get; set; }
    internal Action? OnIdentity;
    internal Action? OnData { get; set; }
    public DataObject Data
    {
        get { OnData?.Invoke(); return _data ?? throw new ObjectDisposedException(nameof(ProbeSourceTestPayload)); }
    }
    public ProbeSourcePayloadIdentity Identity
    {
        get
        {
            OnIdentity?.Invoke();
            return new("fixture", "Single", 1, "source-digest", "occurrence-digest");
        }
    }
    public ProbeSourcePayloadResources Resources => ThrowOnObservation
        ? throw new IOException("Injected resource observation failure.")
        : new(_data is null ? 0 : 1, Preferred.CanRead ? 1 : 0, Marker.CanRead ? 1 : 0);
    public void Dispose()
    {
        DisposeCalls++;
        OnDispose?.Invoke();
        if (ThrowOnDispose) throw new IOException("Injected payload release failure.");
        ForceRelease();
    }
    internal void ForceRelease() { _data = null; Preferred.Dispose(); Marker.Dispose(); }
}

internal sealed class ProbeSourceTestLease : IProbeSourceLease
{
    internal int ReleaseCalls;
    internal Action? OnRelease;
    internal bool ThrowOnRelease;
    internal bool ThrowOnObservation { get; set; }
    internal int RemainingValue = 1;
    public int? Remaining => ThrowOnObservation ? throw new IOException("Injected lease observation failure.") : RemainingValue;
    public void Release()
    {
        ReleaseCalls++;
        OnRelease?.Invoke();
        if (ThrowOnRelease) throw new IOException("Injected lease release failure.");
        RemainingValue = 0;
    }
}

internal sealed class ProbeSourceTestInvoker(Func<DataObject, ProbeSourceCallbacks, DragDropEffects> invoke) : IProbeSourceInvoker
{
    internal int Calls;
    internal DragDropEffects Allowed;
    public DragDropEffects Invoke(DataObject data, DragDropEffects allowedEffects, ProbeSourceCallbacks callbacks)
    {
        Calls++;
        Allowed = allowedEffects;
        return invoke(data, callbacks);
    }
}

internal sealed class ProbeSourceTestRecorder : IProbeSourceRecorder
{
    internal readonly List<string> Names = [];
    internal Action<string>? OnRecord { get; set; }
    internal Action? OnSeal { get; set; }
    internal TaskCompletionSource? ArchiveGate;
    internal int SealCalls;
    internal bool RejectEvents { get; set; }
    internal bool ThrowOnSeal { get; set; }
    private Task<ProbeSourceCompletion>? _completion;
    public Task Ready { get; set; } = Task.CompletedTask;
    public ProbeSourceError? Fault { get; set; }
    public bool TryRecord(string name, string phase, long giveFeedbackCount, long queryContinueDragCount)
    {
        Names.Add(name);
        OnRecord?.Invoke(name);
        return !RejectEvents;
    }
    public Task<ProbeSourceCompletion> SealAndPublishAsync(ProbeSourceReport report)
    {
        if (_completion is not null) return _completion;
        SealCalls++;
        if (ThrowOnSeal) throw new IOException("Injected publication failure.");
        var gate = new TaskCompletionSource<ProbeSourceCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = gate.Task;
        OnSeal?.Invoke();
        _ = CompleteAsync(report, gate);
        return _completion;
    }
    private async Task CompleteAsync(ProbeSourceReport report, TaskCompletionSource<ProbeSourceCompletion> completion)
    {
        if (ArchiveGate is not null) await ArchiveGate.Task;
        var health = new ProbeSourceEvidenceHealth(128, Names.Count, Names.Count, Names.Count, 0, 0, null,
            Names.Count, true, true, true, Array.Empty<ProbeSourceError>());
        completion.SetResult(new(report with { Evidence = health }, true, "memory", null, true));
    }
}

internal static class ProbeSourceTestFiles
{
    internal static ProbeOutputDirectory Reserve(ProbeOutboundTestLayout layout) => ProbeOutputDirectory.Reserve(
        layout.Options with { OutputRoot = Path.Combine(layout.Options.AllowedRoot, $"round-{Guid.NewGuid():N}") },
        new ProbePathEnvironment(Path.Combine(layout.Root, "protected-product"), Path.Combine(layout.Root, "protected-profile")));

    internal static ProbeSourceIdentity Identity() => new(Guid.NewGuid().ToString("N"),
        new(Guid.NewGuid(), 1, Guid.NewGuid()), "test", Environment.ProcessId, Environment.CurrentManagedThreadId);

    internal static ProbeSourceReport Report(ProbeSourceIdentity identity)
    {
        var stamp = ProbeSourceStamp.Capture(TimeProvider.System);
        var resources = new ProbeSourceResourceSnapshot(0, 0, 1, 1, ProbeSourcePayloadResources.Empty,
            "NotApplicable", null, "NotApplicable", null);
        return new(identity, null, stamp, stamp, true, stamp, stamp, "Returned", 1, null,
            new(false, null, null, null, 0, false), new("Simulated", null, null, null, 0, 0, 0),
            resources, resources, Array.Empty<ProbeSourceRelease>(), true, Array.Empty<ProbeSourceError>(), 0);
    }
}

internal sealed class ProbeSourceTestSink : IProbeSourceEventSink
{
    internal readonly List<ProbeSourceEvent> Events = [];
    internal readonly List<int> Threads = [];
    internal Action<ProbeSourceEvent>? OnWrite { get; set; }
    internal string? ThrowStage { get; set; }
    internal int WriteCalls;
    internal int FlushCalls;
    internal int DisposeCalls;
    public void Write(ProbeSourceEvent value)
    {
        WriteCalls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        OnWrite?.Invoke(value);
        if (ThrowStage == "write") throw new IOException("Injected sink write failure.");
        Events.Add(value);
    }
    public void Flush()
    {
        FlushCalls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        if (ThrowStage == "flush") throw new IOException("Injected sink flush failure.");
    }
    public void Dispose()
    {
        DisposeCalls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        if (ThrowStage == "close") throw new IOException("Injected sink close failure.");
    }
}
