using Vok.Infrastructure.Windows.Windowing;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Xunit;

namespace Vok.IntegrationTests.Windowing;

public sealed class Win32WindowPlacementServiceTests
{
    [Fact]
    public void DisplayCatalogReturnsUsableWorkAreasAndDpi()
    {
        var workAreas = new Win32DisplayWorkAreaProvider().GetWorkAreas();

        Assert.NotEmpty(workAreas);
        Assert.All(
            workAreas,
            workArea =>
            {
                Assert.True(workArea.WidthPixels > 0);
                Assert.True(workArea.HeightPixels > 0);
                Assert.True(workArea.DpiX > 0);
                Assert.True(workArea.DpiY > 0);
                Assert.True(workArea.WidthDip > 0);
                Assert.True(workArea.HeightDip > 0);
            });
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040)]
    [InlineData(0, 0, 1920, 1080, 0, 40, 1920, 1080)]
    [InlineData(-1920, 0, 0, 1080, -1880, 0, 0, 1080)]
    [InlineData(0, -1080, 1920, 0, 0, -1080, 1880, 0)]
    public void WorkspaceBoundsUseTheExactSystemWorkAreaForEveryTaskbarEdge(
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        var monitorInfo = new MONITORINFO
        {
            rcMonitor = new RECT
            {
                left = monitorLeft,
                top = monitorTop,
                right = monitorRight,
                bottom = monitorBottom,
            },
            rcWork = new RECT
            {
                left = workLeft,
                top = workTop,
                right = workRight,
                bottom = workBottom,
            },
        };

        var bounds = Win32WindowPlacementService.ResolveWorkspaceBounds(monitorInfo);

        Assert.Equal(workLeft, bounds.left);
        Assert.Equal(workTop, bounds.top);
        Assert.Equal(workRight, bounds.right);
        Assert.Equal(workBottom, bounds.bottom);
    }
}
