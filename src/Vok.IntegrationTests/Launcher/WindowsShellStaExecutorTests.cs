using Vok.Infrastructure.Windows.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Launcher;

public sealed class WindowsShellStaExecutorTests
{
    [Fact]
    public async Task CancellationWinsOnlyBeforeTheSideEffectClaim()
    {
        using var executor = new WindowsShellStaExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = executor.ExecuteAsync(() =>
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return 1;
        }, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        var executionCount = 0;
        var cancelled = executor.ExecuteAsync(
            () =>
            {
                executionCount++;
                return 2;
            },
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.Set();
        Assert.Equal(1, await first);
        Assert.Equal(0, executionCount);
    }

    [Fact]
    public async Task ShutdownWatchdogDoesNotWaitForeverForAClaimedOperation()
    {
        var executor = new WindowsShellStaExecutor(TimeSpan.FromMilliseconds(100));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operation = executor.ExecuteAsync(() =>
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return 7;
        }, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        var dispose = Task.Run(
            executor.Dispose,
            TestContext.Current.CancellationToken);
        await dispose.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.False(operation.IsCompleted);

        release.Set();
        Assert.Equal(
            7,
            await operation.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
    }
}
