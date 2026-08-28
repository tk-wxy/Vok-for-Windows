using Vok.Core.Clipboard;
using Xunit;

namespace Vok.Tests.Clipboard;

public sealed class ClipboardPasteStateMachineTests
{
    [Fact]
    public void HappyPathRequiresEveryOrderedGate()
    {
        var machine = new ClipboardPasteStateMachine();
        var started = machine.Begin(Guid.NewGuid());

        Assert.True(started.Accepted);
        Assert.True(machine.Advance(
            started.Generation,
            ClipboardPasteState.Materializing,
            ClipboardPasteState.WritingClipboard));
        Assert.True(machine.Advance(
            started.Generation,
            ClipboardPasteState.WritingClipboard,
            ClipboardPasteState.HidingWorkspace));
        Assert.True(machine.Advance(
            started.Generation,
            ClipboardPasteState.HidingWorkspace,
            ClipboardPasteState.VerifyingCurrent));
        Assert.True(machine.Advance(
            started.Generation,
            ClipboardPasteState.VerifyingCurrent,
            ClipboardPasteState.InjectingPaste));
        Assert.True(machine.Finish(started.Generation, ClipboardPasteState.Completed));
        Assert.Equal(ClipboardPasteState.Completed, machine.State);
    }

    [Fact]
    public void ConcurrentBeginAndOutOfOrderTransitionAreRejected()
    {
        var machine = new ClipboardPasteStateMachine();
        var first = machine.Begin(Guid.NewGuid());

        Assert.False(machine.Begin(Guid.NewGuid()).Accepted);
        Assert.False(machine.Advance(
            first.Generation,
            ClipboardPasteState.Materializing,
            ClipboardPasteState.HidingWorkspace));
        Assert.Equal(ClipboardPasteState.Materializing, machine.State);
    }

    [Fact]
    public void StaleGenerationCannotFinishNewAction()
    {
        var machine = new ClipboardPasteStateMachine();
        var first = machine.Begin(Guid.NewGuid());
        Assert.True(machine.Finish(first.Generation, ClipboardPasteState.Failed));
        var second = machine.Begin(Guid.NewGuid());

        Assert.False(machine.Finish(first.Generation, ClipboardPasteState.Completed));
        Assert.Equal(second.Generation, machine.ActiveGeneration);
        Assert.Equal(ClipboardPasteState.Materializing, machine.State);
    }

    [Theory]
    [InlineData(ClipboardPasteState.Completed)]
    [InlineData(ClipboardPasteState.CopiedOnly)]
    [InlineData(ClipboardPasteState.Failed)]
    [InlineData(ClipboardPasteState.Cancelled)]
    public void TerminalStateAllowsNextGeneration(ClipboardPasteState terminal)
    {
        var machine = new ClipboardPasteStateMachine();
        var first = machine.Begin(Guid.NewGuid());
        Assert.True(machine.Finish(first.Generation, terminal));

        var second = machine.Begin(Guid.NewGuid());

        Assert.True(second.Accepted);
        Assert.Equal(first.Generation + 1, second.Generation);
    }
}
