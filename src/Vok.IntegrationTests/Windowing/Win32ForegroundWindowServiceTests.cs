using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Windowing;
using Xunit;

namespace Vok.IntegrationTests.Windowing;

public sealed class Win32ForegroundWindowServiceTests
{
    [Fact]
    public void CaptureReturnsInternallyConsistentTarget()
    {
        var service = new Win32ForegroundWindowService();

        var target = service.Capture();

        if (target.CanRestore)
        {
            Assert.NotEqual(0u, target.ProcessId);
            Assert.NotEqual(0u, target.ThreadId);
            if (target.FocusedWindowHandle != 0)
            {
                Assert.NotEqual(0u, target.FocusedProcessId);
                Assert.NotEqual(0u, target.FocusedThreadId);
                Assert.NotEqual(ForegroundWindowRelationship.None, target.FocusedWindowRelationship);
            }
        }
        else
        {
            Assert.Equal(ForegroundTarget.None, target);
        }
    }

    [Fact]
    public void RestoreRejectsMissingTargetWithoutTouchingForeground()
    {
        var service = new Win32ForegroundWindowService();

        var result = service.Restore(ForegroundTarget.None);

        Assert.Equal(ForegroundRestoreStatus.InvalidTarget, result.Status);
        Assert.False(result.Succeeded);
    }
}
