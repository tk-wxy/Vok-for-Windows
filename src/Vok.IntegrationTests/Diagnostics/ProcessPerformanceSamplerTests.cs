using Vok.Infrastructure.Windows.Diagnostics;
using Xunit;

namespace Vok.IntegrationTests.Diagnostics;

public sealed class ProcessPerformanceSamplerTests
{
    [Fact]
    public void CaptureReturnsAConsistentProcessSnapshot()
    {
        var sampler = new ProcessPerformanceSampler();

        var snapshot = sampler.Capture();

        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.PrivateMemoryBytes > 0);
        Assert.True(snapshot.HandleCount > 0);
        Assert.True(snapshot.ThreadCount > 0);
        Assert.InRange(snapshot.UserObjectCount, 0u, 10_000u);
        Assert.InRange(snapshot.GdiObjectCount, 0u, 10_000u);
        Assert.True(snapshot.CapturedAt <= DateTimeOffset.UtcNow);
    }
}
