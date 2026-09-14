using Vok.Core.Diagnostics;
using Vok.Core.Workspace;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed partial class WorkspaceControllerTests
{
    [Fact]
    public async Task TraceCharacterizesUnreadyContentAtUncloakWithoutChangingTheBaselineDecision()
    {
        var trace = new RevealTrace(true);
        using var rig = await TestRig.CreateAsync(trace);
        var contentReady = false;
        rig.Window.RevealObservation = (phase, generation) =>
            trace.Record("TestContent", generation, value1: contentReady ? 1 : 0, detail: phase);
        rig.Placement.OsGatesReady = true;
        rig.Window.FocusSucceeds = true;
        rig.Hotkey.Raise(ExternalTarget);
        Assert.Equal(WorkspaceState.Visible, rig.StateMachine.State);
        var beforeUncloak = Assert.Single(trace.Capture().Entries, entry =>
            entry.Phase == "TestContent" && entry.Detail == "BeforeUncloak");
        Assert.Equal(0, beforeUncloak.Value1);
        contentReady = true;
        // P0.1 characterizes the missing content contract; it must not secretly gate the host.
        Assert.Equal(1, beforeUncloak.Generation);
        AssertOrdered(trace.Capture().Entries.Select(entry => entry.Phase).ToArray(),
            "ShowBegin", "Rendering", "FenceRequested", "FenceReturned", "BeforeUncloak", "Uncloaked", "InputReady");
    }

    [Fact]
    public async Task RenderingOnlyRecordsTimestampAndDoesNotRequestExportOrObserveVisualTree()
    {
        var trace = new RevealTrace(true);
        using var rig = await TestRig.CreateAsync(trace);
        rig.Window.RaiseRenderingAfterShow = false;
        rig.Window.QueueBackgroundPosts = true;
        var observations = 0;
        var exports = 0;
        rig.Window.RevealObservation = (_, _) => observations++;
        trace.ExportRequested += () => exports++;
        rig.Hotkey.Raise(ExternalTarget);
        var observedBeforeFrame = observations;
        rig.Frames.RaiseRendering();
        Assert.Equal(observedBeforeFrame, observations);
        Assert.Equal(0, exports);
        Assert.Contains(trace.Capture().Entries, entry => entry.Phase == "Rendering");
        Assert.DoesNotContain(trace.Capture().Entries, entry => entry.Phase == "FenceRequested");
    }
}
