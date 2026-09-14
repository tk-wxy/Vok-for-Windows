using Vok.Core.Diagnostics;
using Xunit;

namespace Vok.Tests.Diagnostics;

public sealed class RevealTraceTests
{
    [Fact]
    public void RecoveryWatermarksSurviveRingEvictionAndExportFailureIsIsolated()
    {
        var trace = new RevealTrace(true, 2);
        trace.Record("PowerMode", detail: "Resume", timestamp: 100);
        trace.Record("SessionSwitch", detail: "SessionUnlock", timestamp: 200);
        trace.Record("later", timestamp: 300);
        var completed = false;
        trace.ExportRequested += () => throw new IOException("test diagnostic failure");
        trace.ExportRequested += () => completed = true;
        trace.RequestExport();
        var snapshot = trace.Capture();
        Assert.Equal(100, snapshot.LastResumeTimestamp);
        Assert.Equal(200, snapshot.LastUnlockTimestamp);
        Assert.True(completed);
        Assert.Contains(snapshot.Entries, entry => entry.Phase == "TraceExportRequestFailed");
    }

    [Fact]
    public void DisabledTraceDoesNotRetainEventsOrRequestExport()
    {
        var trace = new RevealTrace(false);
        var exports = 0;
        trace.ExportRequested += () => exports++;
        trace.Record("ignored");
        trace.RequestExport();
        Assert.Empty(trace.Capture().Entries);
        Assert.Equal(0, trace.NextId());
        Assert.Equal(0, exports);
    }

    [Fact]
    public void CapacityAndLostHistoryAreExplicitWithoutMixingInstances()
    {
        var trace = new RevealTrace(true, 3);
        for (var index = 1; index <= 5; index++) trace.Record("event", generation: index);
        var snapshot = trace.Capture();
        Assert.Equal(2, snapshot.OverwrittenEntries);
        Assert.Equal(new long[] { 3, 4, 5 }, snapshot.Entries.Select(entry => entry.Generation));
        Assert.Empty(new RevealTrace(true).Capture().Entries);
        trace.Record("later");
        Assert.Equal(5, snapshot.Entries[^1].Sequence);
    }

    [Fact]
    public void ConcurrentProducersKeepUniqueSequencesAndOwnedRequestIds()
    {
        var trace = new RevealTrace(true, 100);
        Parallel.For(0, 1000, index => trace.Record("event", operationId: trace.NextId()));
        var snapshot = trace.Capture();
        Assert.Equal(900, snapshot.OverwrittenEntries);
        Assert.Equal(100, snapshot.Entries.Select(entry => entry.OperationId).Distinct().Count());
        Assert.Equal(Enumerable.Range(901, 100).Select(value => (long)value),
            snapshot.Entries.Select(entry => entry.Sequence));
    }
}
