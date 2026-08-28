using System.Runtime.InteropServices;
using Vok.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Vok.Infrastructure.Windows.Windowing;

public sealed unsafe class Win32DisplayWorkAreaProvider : IDisplayWorkAreaProvider
{
    private const uint DefaultDpi = 96;

    public IReadOnlyList<DisplayWorkArea> GetWorkAreas()
    {
        var workAreas = new List<DisplayWorkArea>();
        MONITORENUMPROC callback = AddMonitor;

        if (!PInvoke.EnumDisplayMonitors(default, null, callback, default))
        {
            throw new InvalidOperationException("Unable to enumerate display work areas.");
        }

        if (workAreas.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report an active display work area.");
        }

        return workAreas;

        BOOL AddMonitor(HMONITOR monitor, HDC deviceContext, RECT* monitorBounds, LPARAM state)
        {
            _ = deviceContext;
            _ = monitorBounds;
            _ = state;

            var monitorInfo = new MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
            };
            if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo))
            {
                return true;
            }

            var dpiX = DefaultDpi;
            var dpiY = DefaultDpi;
            if (OperatingSystem.IsWindowsVersionAtLeast(8, 1))
            {
                var dpiResult = PInvoke.GetDpiForMonitor(
                    monitor,
                    MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                    out var reportedDpiX,
                    out var reportedDpiY);
                if (dpiResult.Succeeded && reportedDpiX != 0 && reportedDpiY != 0)
                {
                    dpiX = reportedDpiX;
                    dpiY = reportedDpiY;
                }
            }

            var bounds = monitorInfo.rcWork;
            workAreas.Add(new DisplayWorkArea(
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                dpiX,
                dpiY));
            return true;
        }
    }
}
