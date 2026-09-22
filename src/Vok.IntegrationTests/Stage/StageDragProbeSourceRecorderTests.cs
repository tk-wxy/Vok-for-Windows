using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageDragProbeSourceRecorderTests(WpfApplicationFixture wpf)
{
    private Task Run(Func<Task> action) => wpf.InvokeAsync(async () => { await action(); return true; });

    [Fact]
    public Task RealRecorderDrainsClosesAndPublishesBeforeReadmission() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var source = new ProbeOutboundDragSession("test");
        var output = ProbeSourceTestFiles.Reserve(layout);
        ProbeSourceEventRecorder? recorder = null;
        var ticket = (await source.TryPrepareAsync(id => recorder = new(id, output), () => new ProbeSourceTestPayload()))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, c) =>
        {
            c.GiveFeedback(DragDropEffects.Copy);
            c.QueryContinueDrag(false, DragDropKeyStates.None);
            return DragDropEffects.Copy;
        }));
        await source.Completion;
        var result = source.LastCompletion!;
        Assert.True(result.Succeeded);
        Assert.Equal(ProbeSourcePhase.Idle, source.Phase);
        Assert.Equal(result.Report, ProbeReportWriter.Read<ProbeSourceReport>(result.Path!) with
        {
            Errors = result.Report.Errors, Releases = result.Report.Releases, Evidence = result.Report.Evidence,
        });
        var lines = File.ReadAllLines(Path.Combine(output.Path, "events.jsonl"));
        Assert.Equal(result.Report.Evidence!.Written, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var json = JsonDocument.Parse(lines[index]);
            Assert.Equal(index + 1, json.RootElement.GetProperty("sequence").GetInt64());
            Assert.Equal("SourceSessionSimulation", json.RootElement.GetProperty("evidenceScope").GetString());
        }
        var task = recorder!.SealAndPublishAsync(result.Report);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Same(task, recorder.SealAndPublishAsync(result.Report));
        Assert.False(recorder.TryRecord("Late", "Idle", 0, 0));
        using var exclusive = new FileStream(Path.Combine(output.Path, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
        Assert.Single(Directory.GetFiles(output.Path, "report.json"));
        Assert.Empty(Directory.GetFiles(output.Path, "*.pending"));
    });

    [Fact]
    public Task FullQueueNeverWaitsForSinkAndSealCannotBeBlockedByCapacity() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ProbeSourceTestSink { OnWrite = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test sink was not released.");
        } };
        var identity = ProbeSourceTestFiles.Identity();
        var recorder = new ProbeSourceEventRecorder(identity, ProbeSourceTestFiles.Reserve(layout), 1, openSink: () => sink);
        Task<ProbeSourceCompletion>? archive = null;
        try
        {
            await recorder.Ready;
            Assert.True(recorder.TryRecord("First", "Invoking", 1, 0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(recorder.TryRecord("Second", "Invoking", 2, 0));
            Assert.False(recorder.TryRecord("Overflow", "Invoking", 3, 0));
            Assert.NotNull(recorder.Fault);
            archive = recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
            Assert.False(archive.IsCompleted);
            Assert.Same(archive, recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity)));
            await wpf.Application.Dispatcher.InvokeAsync(() => Assert.False(archive.IsCompleted));
        }
        finally
        {
            release.Set();
            archive ??= recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
            await archive;
        }
        var result = await archive;
        Assert.False(result.Succeeded);
        Assert.True(result.Published);
        Assert.Equal(1, result.Report.RawEffect);
        var health = result.Report.Evidence!;
        Assert.Equal(3, health.Produced);
        Assert.Equal(2, health.Written);
        Assert.Equal(1, health.Dropped);
        Assert.Equal(3, health.FirstGapSequence);
        Assert.Equal(1, health.HighWater);
        Assert.Equal(0, health.Unwritten);
        Assert.All(sink.Threads, thread => Assert.NotEqual(identity.OwnerThreadId, thread));
        Assert.Equal(1, sink.DisposeCalls);
    });

    [Theory]
    [InlineData("open", "EventOpen")]
    [InlineData("write", "EventWrite")]
    [InlineData("flush", "EventFlush")]
    [InlineData("close", "EventClose")]
    public Task SinkFailuresAreIndependentFromRawCopyAndDoNotSkipClose(string failure, string stage) => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        var identity = ProbeSourceTestFiles.Identity();
        var sink = new ProbeSourceTestSink { ThrowStage = failure };
        var recorder = new ProbeSourceEventRecorder(identity, ProbeSourceTestFiles.Reserve(layout), openSink: () =>
            failure == "open" ? throw new IOException("Injected open failure") : sink);
        if (failure == "open") await Assert.ThrowsAsync<IOException>(() => recorder.Ready);
        else await recorder.Ready;
        recorder.TryRecord("Sample", "Invoking", 0, 0);
        var result = await recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
        Assert.True(result.Published);
        Assert.True(result.WriterStopped);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Report.RawEffect);
        Assert.Contains(result.Report.Evidence!.Errors, e => e.Stage == stage);
        Assert.Equal(failure == "open" ? 0 : 1, sink.FlushCalls);
        Assert.Equal(failure == "open" ? 0 : 1, sink.DisposeCalls);
        Assert.EndsWith("failure.json", result.Path);
    });

    [Fact]
    public Task PartialLineFailureStopsAllFurtherWritesAndPreservesTheTail() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        var output = ProbeSourceTestFiles.Reserve(layout);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = ProbeSourceTestFiles.Identity();
        PartialSink? sink = null;
        var recorder = new ProbeSourceEventRecorder(identity, output, openSink: () => sink = new(output, entered, release));
        Task<ProbeSourceCompletion>? archive = null;
        try
        {
            await recorder.Ready;
            Assert.True(recorder.TryRecord("First", "Invoking", 0, 0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(recorder.TryRecord("Second", "Invoking", 0, 0));
            Assert.True(recorder.TryRecord("Third", "Invoking", 0, 0));
            archive = recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
        }
        finally
        {
            release.Set();
            archive ??= recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
            await archive;
        }
        var result = await archive;
        Assert.Equal(1, sink!.Calls);
        Assert.Equal(0, result.Report.Evidence!.Written);
        Assert.Equal(3, result.Report.Evidence.Unwritten);
        Assert.Equal(1, result.Report.Evidence.FirstGapSequence);
        Assert.Equal("{\"partial\":", File.ReadAllText(Path.Combine(output.Path, "events.jsonl")));
        Assert.True(result.Published);
        Assert.False(result.Succeeded);
    });

    [Theory]
    [InlineData("throw")]
    [InlineData("existing-file")]
    [InlineData("existing-directory")]
    public Task PublicationFailureHasOneReceiptAndNeverOverwritesEvidence(string failure) => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        var output = ProbeSourceTestFiles.Reserve(layout);
        var identity = ProbeSourceTestFiles.Identity();
        var calls = 0;
        var recorder = new ProbeSourceEventRecorder(identity, output, publish: failure == "throw"
            ? _ => { calls++; throw new IOException("Injected terminal failure"); } : null);
        await recorder.Ready;
        var protectedPath = Path.Combine(output.Path, "report.json");
        if (failure == "existing-file") File.WriteAllText(protectedPath, "untouched");
        if (failure == "existing-directory") Directory.CreateDirectory(protectedPath);
        recorder.TryRecord("Sample", "Invoking", 0, 0);
        var first = recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity));
        Assert.Same(first, recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity)));
        var result = await first;
        Assert.False(result.Published);
        Assert.True(result.WriterStopped);
        Assert.Equal(1, result.Report.RawEffect);
        Assert.Equal("Publication", result.PublicationError!.Stage);
        Assert.Equal(failure == "throw" ? 1 : 0, calls);
        Assert.False(File.Exists(Path.Combine(output.Path, "failure.json")));
        if (failure == "existing-file") Assert.Equal("untouched", File.ReadAllText(protectedPath));
        if (failure == "existing-directory") Assert.True(Directory.Exists(protectedPath));
        using var stream = new FileStream(Path.Combine(output.Path, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None);
    });

    [Fact]
    public Task ProducerOwnsStampsAndForeignThreadOrRoundCannotMutateRecorder() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        var identity = ProbeSourceTestFiles.Identity();
        var sink = new ProbeSourceTestSink();
        var recorder = new ProbeSourceEventRecorder(identity, ProbeSourceTestFiles.Reserve(layout),
            clock: new ReversingWallClock(), openSink: () => sink);
        await recorder.Ready;
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => recorder.TryRecord("Foreign", "Ready", 0, 0));
            Assert.Throws<InvalidOperationException>(() => { _ = recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(identity)); });
        });
        Assert.Throws<InvalidOperationException>(() => { _ = recorder.SealAndPublishAsync(ProbeSourceTestFiles.Report(ProbeSourceTestFiles.Identity())); });
        recorder.TryRecord("First", "Invoking", 1, 0);
        recorder.TryRecord("Second", "Invoking", 2, 0);
        var errors = new List<ProbeSourceError>();
        var report = ProbeSourceTestFiles.Report(identity) with { Errors = errors };
        var task = recorder.SealAndPublishAsync(report);
        errors.Add(ProbeSourceError.Failure("Mutation", "Must not affect sealed report"));
        var result = await task;
        Assert.True(result.Succeeded);
        Assert.Empty(result.Report.Errors);
        Assert.Equal(2, sink.Events.Count);
        Assert.True(sink.Events[0].Stamp.Ticks < sink.Events[1].Stamp.Ticks);
        Assert.True(sink.Events[0].Stamp.Utc > sink.Events[1].Stamp.Utc);
        Assert.Equal(1000, sink.Events[0].Stamp.Frequency);
        Assert.Equal(1, sink.Events[0].GiveFeedbackCount);
        Assert.Equal(2, sink.Events[1].GiveFeedbackCount);
        Assert.All(sink.Threads, thread => Assert.NotEqual(identity.OwnerThreadId, thread));
    });

    [Theory]
    [InlineData("flush")]
    [InlineData("close")]
    [InlineData("publication")]
    public Task SourceKeepsCopyButFailsGateWhenRealWriterCompletionIsUnhealthy(string failure) => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var source = new ProbeOutboundDragSession("test");
        var sink = new ProbeSourceTestSink { ThrowStage = failure };
        var output = ProbeSourceTestFiles.Reserve(layout);
        var ticket = (await source.TryPrepareAsync(id => new ProbeSourceEventRecorder(id, output, openSink: () => sink,
            publish: failure == "publication" ? _ => throw new IOException("Publication failed") : null), () => layout.CreateData()))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) =>
        {
            source.RequestStop(ProbeSourceStopReason.Close);
            return DragDropEffects.Copy;
        }));
        await source.Completion;
        Assert.Equal(1, source.LastCompletion!.Report.RawEffect);
        Assert.True(source.LastCompletion.Report.CleanupSucceeded);
        Assert.False(source.LastCompletion.Succeeded);
        Assert.Equal(ProbeSourcePhase.Faulted, source.Phase);
        Assert.Equal(failure != "close", source.IsFullyDisposed);
        Assert.Null(await source.TryPrepareAsync(_ => throw new Exception("No restart"), () => layout.CreateData()));
    });

    [Fact]
    public Task ActualWorkerStillOwnsAdmissionAfterPayloadIsCleaned() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var source = new ProbeOutboundDragSession("test");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new ProbeSourceTestSink { OnWrite = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test sink was not released.");
        } };
        ProbeOutboundDataObject? payload = null;
        var ticket = (await source.TryPrepareAsync(id => new ProbeSourceEventRecorder(id, ProbeSourceTestFiles.Reserve(layout), openSink: () => sink),
            () => payload = layout.CreateData()))!.Value;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, _) => DragDropEffects.Copy));
            Assert.True(payload!.IsDisposed);
            Assert.True(((IProbeSourcePayload)payload).Resources.IsEmpty);
            Assert.False(source.Completion.IsCompleted);
            Assert.Equal(ProbeSourcePhase.Archiving, source.Phase);
            await wpf.Application.Dispatcher.InvokeAsync(() => Assert.False(source.Completion.IsCompleted));
            Assert.Null(await source.TryPrepareAsync(_ => throw new Exception("Second recorder"), () => layout.CreateData()));
        }
        finally
        {
            release.Set();
            source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
            await source.Completion;
        }
        Assert.True(source.LastCompletion!.Succeeded);
        Assert.True(source.LastCompletion.WriterStopped);
        Assert.Equal(1, sink.DisposeCalls);
        Assert.Equal(ProbeSourcePhase.Idle, source.Phase);
    });

    private sealed class ReversingWallClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ++_ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(-_ticks);
    }

    private sealed class PartialSink(ProbeOutputDirectory output, TaskCompletionSource entered, ManualResetEventSlim release) : IProbeSourceEventSink
    {
        private readonly FileStream _stream = new(Path.Combine(output.Path, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        internal int Calls;
        public void Write(ProbeSourceEvent value)
        {
            Calls++;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test writer was not released.");
            _stream.Write(Encoding.UTF8.GetBytes("{\"partial\":"));
            throw new IOException("Injected partial record write");
        }
        public void Flush() => _stream.Flush(true);
        public void Dispose() => _stream.Dispose();
    }
}
