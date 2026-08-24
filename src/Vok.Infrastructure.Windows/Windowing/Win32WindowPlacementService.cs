using System.Runtime.InteropServices;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Windowing;

public sealed unsafe class Win32WindowPlacementService : IWindowPlacementService
{
    private static readonly HWND TopmostWindow = new((void*)(-1));
    private static readonly HWND NotTopmostWindow = new((void*)(-2));

    public void ApplyFullScreen(nint workspaceWindow, ForegroundTarget target)
    {
        if (workspaceWindow == 0)
        {
            throw new ArgumentException("The workspace HWND is not initialized.", nameof(workspaceWindow));
        }

        var monitor = target.CanRestore
            ? PInvoke.MonitorFromWindow(
                new HWND((void*)target.WindowHandle),
                MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST)
            : MonitorNearestCursor();

        var monitorInfo = new MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
        };

        if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Unable to resolve the target monitor bounds.");
        }

        var bounds = ResolveWorkspaceBounds(monitorInfo);
        if (!PInvoke.SetWindowPos(
                new HWND((void*)workspaceWindow),
                TopmostWindow,
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE))
        {
            throw new InvalidOperationException("Unable to apply the target monitor work area.");
        }
    }

    internal static RECT ResolveWorkspaceBounds(MONITORINFO monitorInfo) => monitorInfo.rcWork;

    public bool RequestForeground(nint workspaceWindow)
    {
        if (workspaceWindow == 0)
        {
            return false;
        }

        return PInvoke.SetForegroundWindow(new HWND((void*)workspaceWindow));
    }

    public WorkspacePresentationObservation Observe(nint workspaceWindow)
    {
        if (workspaceWindow == 0)
        {
            return default;
        }

        var workspace = new HWND((void*)workspaceWindow);
        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThreadId = foreground == HWND.Null
            ? 0
            : PInvoke.GetWindowThreadProcessId(foreground, out _);
        var focused = foregroundThreadId == 0
            ? HWND.Null
            : GetFocusedWindow(foregroundThreadId);
        var extendedStyle = unchecked((uint)PInvoke.GetWindowLong(
            workspace,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));

        return new WorkspacePresentationObservation(
            PInvoke.IsWindowVisible(workspace),
            (extendedStyle & 0x00000008u) != 0,
            foreground == workspace,
            focused == workspace,
            (nint)foreground.Value,
            (nint)focused.Value);
    }

    public void ExitTopmost(nint workspaceWindow)
    {
        if (workspaceWindow == 0)
        {
            return;
        }

        if (!PInvoke.SetWindowPos(
                new HWND((void*)workspaceWindow),
                NotTopmostWindow,
                0,
                0,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE))
        {
            throw new InvalidOperationException("Unable to leave the topmost window band.");
        }
    }

    private static HWND GetFocusedWindow(uint threadId)
    {
        var info = new GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>(),
        };

        return PInvoke.GetGUIThreadInfo(threadId, &info)
            ? info.hwndFocus
            : HWND.Null;
    }

    private static HMONITOR MonitorNearestCursor()
    {
        if (!PInvoke.GetCursorPos(out var cursor))
        {
            cursor = default;
        }

        return PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
    }
}
