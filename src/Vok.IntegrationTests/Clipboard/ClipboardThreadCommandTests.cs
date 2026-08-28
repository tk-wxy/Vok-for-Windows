using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardThreadCommandTests
{
    [Fact]
    public async Task CancellationBeforeClaimPreventsClipboardSideEffect()
    {
        var callCount = 0;
        using var cancellation = new CancellationTokenSource();
        var command = new ClipboardThreadCommand<int>(
            () => ++callCount,
            cancellation.Token);

        cancellation.Cancel();

        Assert.False(command.TryClaim());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.Task);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task CancellationAfterClaimCannotHideCommittedSideEffect()
    {
        var callCount = 0;
        using var cancellation = new CancellationTokenSource();
        var command = new ClipboardThreadCommand<int>(
            () => ++callCount,
            cancellation.Token);
        Assert.True(command.TryClaim());

        cancellation.Cancel();
        command.ExecuteClaimed();

        Assert.Equal(1, await command.Task);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PostingFailureCompletesCommandWithoutExecutingItLater()
    {
        var callCount = 0;
        var command = new ClipboardThreadCommand<int>(
            () => ++callCount,
            CancellationToken.None);

        command.FailBeforeExecution(new InvalidOperationException("post failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => command.Task);
        Assert.Equal("post failed", exception.Message);
        Assert.False(command.TryClaim());
        Assert.Equal(0, callCount);
    }
}
