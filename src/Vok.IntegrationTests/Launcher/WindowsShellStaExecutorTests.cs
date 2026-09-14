using Vok.Infrastructure.Windows.Launcher;
using Vok.Core.Diagnostics;
using Xunit;

namespace Vok.IntegrationTests.Launcher;

public sealed class WindowsShellStaExecutorTests
{
    [Fact]
    public async Task TraceSeparatesQueuedCancellationFromAnAlreadyRunningInspection()
    {
        var trace = new RevealTrace(true);
        using var executor = new WindowsShellStaExecutor(trace);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var runningCancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var running = executor.ExecuteAsync(() =>
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return 1;
        }, runningCancellation.Token, "StageAvailability", id, 3);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            using var queuedCancellation = new CancellationTokenSource();
            var queued = executor.ExecuteAsync(() => 2, queuedCancellation.Token, "Other");
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            runningCancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Contains(trace.Capture().Entries, entry => entry.Phase == "ShellCancelledBeforeStart");
            Assert.DoesNotContain(trace.Capture().Entries, entry => entry.Phase == "ShellCompleted");
        }
        finally { release.Set(); }
        Assert.Equal(1, await running);
        await executor.ExecuteAsync(() => true, TestContext.Current.CancellationToken);
        var entries = trace.Capture().Entries;
        var started = Assert.Single(entries, entry => entry.Phase == "ShellStarted" && entry.ItemId == id);
        var completed = Assert.Single(entries, entry => entry.Phase == "ShellCompleted" && entry.ItemId == id);
        Assert.Equal(started.OperationId, completed.OperationId);
        Assert.Equal(3, completed.Revision);
        Assert.True(completed.Timestamp >= started.Timestamp);
        Assert.Contains(entries, entry => entry.Phase == "ShellSkipped");
    }

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
