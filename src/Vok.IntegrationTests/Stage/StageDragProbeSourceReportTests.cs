using System.IO;
using System.Text.Json;
using System.Windows;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageDragProbeSourceReportTests(WpfApplicationFixture wpf)
{
    private Task Run(Func<Task> action) => wpf.InvokeAsync(async () => { await action(); return true; });

    [Theory]
    [InlineData("Single")]
    [InlineData("Batch10")]
    [InlineData("Batch100")]
    [InlineData("Mixed")]
    [InlineData("Duplicate")]
    public Task RealFixturePayloadStaysReadableUntilFakeReturnsAndReportIsExplicitlySimulation(string profile) => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create(Enum.Parse<ProbeFixtureProfile>(profile));
        using var source = new ProbeOutboundDragSession(layout.Options.SourceRevision);
        var output = ProbeSourceTestFiles.Reserve(layout);
        ProbeOutboundDataObject? payload = null;
        MemoryStream? preferred = null;
        MemoryStream? marker = null;
        var ticket = (await source.TryPrepareAsync(id => new ProbeSourceEventRecorder(id, output), () =>
        {
            payload = layout.CreateData();
            preferred = Assert.IsType<MemoryStream>(payload.Data.GetData(ProbeOutboundDataObject.PreferredDropEffectFormat, false));
            marker = Assert.IsType<MemoryStream>(payload.Data.GetData(payload.TokenFormat, false));
            return payload;
        }))!.Value;
        source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((data, callbacks) =>
        {
            callbacks.GiveFeedback(DragDropEffects.Copy);
            source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
            Assert.Equal(ProbeSourceDragDecision.Cancel, callbacks.QueryContinueDrag(false, DragDropKeyStates.LeftMouseButton));
            Assert.Equal(layout.Bundle.OrderedPaths, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop, false)));
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, preferred!.ToArray());
            Assert.Equal(new byte[] { 1 }, marker!.ToArray());
            Assert.True(preferred.CanRead);
            Assert.True(marker.CanRead);
            Assert.Equal(new ProbeSourcePayloadResources(1, 1, 1), ((IProbeSourcePayload)payload!).Resources);
            return DragDropEffects.Copy;
        }));
        await source.Completion;
        Assert.True(source.LastCompletion!.Succeeded);
        Assert.True(payload!.IsDisposed);
        Assert.False(preferred!.CanRead);
        Assert.False(marker!.CanRead);
        Assert.Equal(ProbeSourcePayloadResources.Empty, ((IProbeSourcePayload)payload).Resources);
        var report = ProbeReportWriter.Read<ProbeSourceReport>(source.LastCompletion.Path!);
        Assert.Equal("SourceSessionSimulation", report.EvidenceScope);
        Assert.Equal("Fake", report.InvocationKind);
        Assert.False(report.PhysicalOleInvoked);
        Assert.Equal("NotExecuted", report.WindowObservation);
        Assert.Equal("NotExecuted", report.TargetObservation);
        Assert.Equal("NotCaptured", report.UserVerdict);
        Assert.Equal("NotApplicable", report.ProductClaim);
        Assert.Equal("NotApplicable", report.RenderPump);
        Assert.Equal(layout.Bundle.Manifest.Profile, report.Payload!.Profile);
        Assert.Equal(layout.Bundle.Manifest.RunId, report.Payload.FixtureRunId);
        Assert.Equal(layout.Bundle.OrderedPaths.Count, report.Payload.OccurrenceCount);
        Assert.Equal(1, report.RawEffect);
        Assert.True(report.Cancellation.Requested);
        Assert.True(report.CleanupSucceeded);
        Assert.Equal(0, report.AfterCleanup.PreparedPayloadOwners);
        Assert.Equal("NotApplicable", report.AfterCleanup.CaptureScope);
        Assert.Null(report.AfterCleanup.CaptureRemaining);
        var after = ProbeFixtureManifestStore.LoadAndValidate(layout.Output, layout.Options.Profile!.Value, layout.Options.SourceRevision);
        Assert.Equal(report.Payload.SourceTreeDigest, after.Manifest.SourceTreeDigest);
        Assert.Equal(report.Payload.OrderedOccurrenceDigest, after.Manifest.OrderedOccurrenceDigest);
    });

    [Fact]
    public Task TwentyRoundsReuseOneManagerAndTokenWithFreshPublishersAndZeroBetweenRounds() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create(ProbeFixtureProfile.Batch100);
        using var source = new ProbeOutboundDragSession("twenty-simulated-rounds");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid? managerId = null;
        string? runId = null;
        var feedbackSum = 0L;
        for (var round = 1; round <= 20; round++)
        {
            if (source.LastCompletion is { } previous)
            {
                Assert.True(previous.Report.AfterCleanup.Payload.IsEmpty);
                Assert.Equal(0, previous.Report.AfterCleanup.PreparedPayloadOwners);
                Assert.True(previous.WriterStopped);
            }
            var output = ProbeSourceTestFiles.Reserve(layout);
            var ticket = (await source.TryPrepareAsync(id => new ProbeSourceEventRecorder(id, output), () =>
            {
                var payload = layout.CreateData();
                Assert.Equal(ProbeOutboundDragToken.Current.FormatName, payload.TokenFormat);
                return payload;
            }))!.Value;
            managerId ??= ticket.ManagerId;
            Assert.Equal(managerId, ticket.ManagerId);
            Assert.Equal(round, ticket.Generation);
            var effect = round % 2 == 1 ? DragDropEffects.Copy : DragDropEffects.None;
            source.TryInvokePrepared(ticket, new ProbeSourceTestInvoker((_, c) =>
            {
                for (var index = 0; index < round; index++) c.GiveFeedback(effect);
                return effect;
            }));
            await source.Completion;
            var result = source.LastCompletion!;
            Assert.True(result.Succeeded);
            Assert.True(paths.Add(result.Path!));
            runId ??= result.Report.Identity.RunId;
            Assert.Equal(runId, result.Report.Identity.RunId);
            Assert.Equal(round, result.Report.Callbacks.GiveFeedbackCount);
            feedbackSum += result.Report.Callbacks.GiveFeedbackCount;
            Assert.Equal(0, result.Report.AfterCleanup.ActiveDragSessions);
            Assert.Equal(0, result.Report.AfterCleanup.PreparedPayloadOwners);
            Assert.True(result.Report.AfterCleanup.Payload.IsEmpty);
        }
        Assert.Equal(210, feedbackSum);
        Assert.Equal(20, paths.Count);
        Assert.Null(source.ActiveTicket);
        Assert.Equal(ProbeSourcePhase.Idle, source.Phase);
        source.Dispose();
        Assert.True(source.IsFullyDisposed);
    });

    [Fact]
    public Task PrecancelReportDoesNotTurnUnobservedValuesIntoPhysicalZeros() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var source = new ProbeOutboundDragSession("test");
        var output = ProbeSourceTestFiles.Reserve(layout);
        var ticket = (await source.TryPrepareAsync(id => new ProbeSourceEventRecorder(id, output), () => layout.CreateData()))!.Value;
        source.RequestCancel(ticket, ProbeSourceCancelReason.Requested);
        await source.Completion;
        var report = ProbeReportWriter.Read<ProbeSourceReport>(source.LastCompletion!.Path!);
        Assert.True(report.Succeeded);
        Assert.False(report.InvocationEntered);
        Assert.Null(report.RawEffect);
        Assert.Null(report.EnteredAt);
        Assert.Null(report.Callbacks.StartObserved);
        Assert.Equal("NotObserved", report.EffectObservation);
        Assert.Equal("NotExecuted", report.Callbacks.Observation);
        Assert.Equal("NotExecuted", report.PhysicalSourceComparison);
        Assert.EndsWith("report.json", source.LastCompletion.Path);
        using var json = JsonDocument.Parse(File.ReadAllText(source.LastCompletion.Path!));
        Assert.False(json.RootElement.TryGetProperty("rawEffect", out _));
        Assert.Equal("NotObserved", json.RootElement.GetProperty("effectObservation").GetString());
    });

    [Fact]
    public Task ErrorSummariesAreBoundedAndNeverSerializeExceptionGraphs() => Run(() =>
    {
        var error = ProbeSourceError.From("Injected", new IOException(new string('x', 10_000), new Exception("Inner")));
        Assert.Equal(ProbeSourceError.MaxErrorTextChars, error.Message.Length);
        var json = JsonSerializer.Serialize(error);
        Assert.DoesNotContain("InnerException", json);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RealPayloadIdentityAndResourceObservationsAreAlsoOwnerStaOnly() => Run(async () =>
    {
        using var layout = ProbeOutboundTestLayout.Create();
        using var payload = layout.CreateData();
        var borrowed = (IProbeSourcePayload)payload;
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => borrowed.Identity);
            Assert.Throws<InvalidOperationException>(() => borrowed.Resources);
        });
        Assert.Equal(new ProbeSourcePayloadResources(1, 1, 1), borrowed.Resources);
    });
}
