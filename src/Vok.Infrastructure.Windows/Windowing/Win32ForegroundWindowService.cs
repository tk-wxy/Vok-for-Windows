using System.Runtime.InteropServices;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Windowing;

public sealed unsafe class Win32ForegroundWindowService : IForegroundWindowService
{
    private readonly IChildFocusRecovery _childFocusRecovery;

    public Win32ForegroundWindowService()
        : this(new Win32ChildFocusRecovery(new Win32ChildFocusRecoveryNativeApi()))
    {
    }

    internal Win32ForegroundWindowService(IChildFocusRecovery childFocusRecovery)
    {
        _childFocusRecovery = childFocusRecovery;
    }

    public ForegroundTarget Capture()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window == HWND.Null)
        {
            return ForegroundTarget.None;
        }

        var threadId = PInvoke.GetWindowThreadProcessId(window, out var processId);
        var focusedWindow = GetFocusedWindow(threadId);
        var focusedThreadId = GetWindowIdentity(focusedWindow, out var focusedProcessId);
        return new ForegroundTarget(
            (nint)window.Value,
            processId,
            threadId,
            (nint)focusedWindow.Value,
            focusedProcessId,
            focusedThreadId,
            GetRelationship(window, focusedWindow));
    }

    public ForegroundRestoreResult Restore(ForegroundTarget target)
    {
        if (!target.CanRestore)
        {
            return CreateResult(
                target,
                ForegroundRestoreStatus.InvalidTarget,
                HWND.Null,
                setForegroundAttempted: false,
                setForegroundSucceeded: false,
                targetWindowValid: false,
                targetIdentityMatches: false,
                targetProcessId: 0,
                targetThreadId: 0);
        }

        var targetWindow = new HWND((void*)target.WindowHandle);
        var targetWindowValid = PInvoke.IsWindow(targetWindow);
        if (!targetWindowValid)
        {
            return CreateResult(
                target,
                ForegroundRestoreStatus.InvalidTarget,
                PInvoke.GetForegroundWindow(),
                setForegroundAttempted: false,
                setForegroundSucceeded: false,
                targetWindowValid: false,
                targetIdentityMatches: false,
                targetProcessId: 0,
                targetThreadId: 0);
        }

        var currentThreadId = PInvoke.GetWindowThreadProcessId(targetWindow, out var currentProcessId);
        var targetIdentityMatches =
            currentThreadId == target.ThreadId && currentProcessId == target.ProcessId;
        if (!targetIdentityMatches)
        {
            return CreateResult(
                target,
                ForegroundRestoreStatus.InvalidTarget,
                PInvoke.GetForegroundWindow(),
                setForegroundAttempted: false,
                setForegroundSucceeded: false,
                targetWindowValid,
                targetIdentityMatches,
                currentProcessId,
                currentThreadId);
        }

        if (PInvoke.IsIconic(targetWindow))
        {
            _ = PInvoke.ShowWindow(targetWindow, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        var before = PInvoke.GetForegroundWindow();
        if (before == targetWindow)
        {
            return CreateConfirmedResult(
                target,
                ForegroundRestoreStatus.AlreadyForeground,
                before,
                setForegroundAttempted: false,
                setForegroundSucceeded: false,
                targetWindowValid,
                targetIdentityMatches,
                currentProcessId,
                currentThreadId);
        }

        var setForegroundSucceeded = PInvoke.SetForegroundWindow(targetWindow);
        if (!setForegroundSucceeded)
        {
            return CreateResult(
                target,
                ForegroundRestoreStatus.RequestDenied,
                PInvoke.GetForegroundWindow(),
                setForegroundAttempted: true,
                setForegroundSucceeded,
                targetWindowValid,
                targetIdentityMatches,
                currentProcessId,
                currentThreadId);
        }

        var observed = PInvoke.GetForegroundWindow();
        return observed == targetWindow
            ? CreateConfirmedResult(
                target,
                ForegroundRestoreStatus.Confirmed,
                observed,
                setForegroundAttempted: true,
                setForegroundSucceeded,
                targetWindowValid,
                targetIdentityMatches,
                currentProcessId,
                currentThreadId)
            : CreateResult(
                target,
                ForegroundRestoreStatus.DifferentForeground,
                observed,
                setForegroundAttempted: true,
                setForegroundSucceeded,
                targetWindowValid,
                targetIdentityMatches,
                currentProcessId,
                currentThreadId);
    }

    private ForegroundRestoreResult CreateConfirmedResult(
        ForegroundTarget target,
        ForegroundRestoreStatus requestedStatus,
        HWND observedForeground,
        bool setForegroundAttempted,
        bool setForegroundSucceeded,
        bool targetWindowValid,
        bool targetIdentityMatches,
        uint targetProcessId,
        uint targetThreadId)
    {
        var observedFocus = GetFocusedWindow(target.ThreadId);
        var focusRecovery = target.FocusedWindowHandle != 0 &&
                            target.FocusedWindowHandle != (nint)observedFocus.Value
            ? _childFocusRecovery.TryRestore(target)
            : default;
        return CreateResult(
            target,
            requestedStatus,
            observedForeground,
            setForegroundAttempted,
            setForegroundSucceeded,
            targetWindowValid,
            targetIdentityMatches,
            targetProcessId,
            targetThreadId,
            focusRecovery);
    }

    private static ForegroundRestoreResult CreateResult(
        ForegroundTarget target,
        ForegroundRestoreStatus requestedStatus,
        HWND observedForeground,
        bool setForegroundAttempted,
        bool setForegroundSucceeded,
        bool targetWindowValid,
        bool targetIdentityMatches,
        uint targetProcessId,
        uint targetThreadId,
        ChildFocusRecoveryResult focusRecovery = default)
    {
        var targetWindow = new HWND((void*)target.WindowHandle);
        if (focusRecovery.Attempted)
        {
            observedForeground = PInvoke.GetForegroundWindow();
        }

        var capturedFocus = new HWND((void*)target.FocusedWindowHandle);
        var capturedFocusValid = target.FocusedWindowHandle != 0 && PInvoke.IsWindow(capturedFocus);
        var capturedFocusThreadId = GetWindowIdentity(capturedFocusValid ? capturedFocus : HWND.Null, out var capturedFocusProcessId);
        var capturedFocusIdentityMatches = capturedFocusValid &&
            capturedFocusProcessId == target.FocusedProcessId &&
            capturedFocusThreadId == target.FocusedThreadId;
        var capturedFocusRelationship = capturedFocusValid
            ? GetRelationship(targetWindow, capturedFocus)
            : ForegroundWindowRelationship.None;

        var observedFocus = target.ThreadId == 0
            ? HWND.Null
            : GetFocusedWindow(target.ThreadId);
        var observedFocusThreadId = GetWindowIdentity(observedFocus, out var observedFocusProcessId);
        var observedFocusRelationship = GetRelationship(targetWindow, observedFocus);
        var foregroundConfirmed = requestedStatus is
            ForegroundRestoreStatus.Confirmed or ForegroundRestoreStatus.AlreadyForeground;
        var status = focusRecovery.DetachAttempted && !focusRecovery.DetachSucceeded
            ? ForegroundRestoreStatus.FocusRecoveryDetachFailed
            : foregroundConfirmed && observedForeground != targetWindow
                ? ForegroundRestoreStatus.DifferentForeground
            : foregroundConfirmed &&
              target.FocusedWindowHandle != 0 &&
              target.FocusedWindowHandle != (nint)observedFocus.Value
                ? ForegroundRestoreStatus.ForegroundConfirmedFocusChanged
                : requestedStatus;

        return new ForegroundRestoreResult(
            status,
            (nint)observedForeground.Value,
            (nint)observedFocus.Value,
            setForegroundAttempted,
            setForegroundSucceeded,
            targetWindowValid,
            targetIdentityMatches,
            targetProcessId,
            targetThreadId,
            capturedFocusValid,
            capturedFocusIdentityMatches,
            capturedFocusProcessId,
            capturedFocusThreadId,
            capturedFocusRelationship,
            observedFocusProcessId,
            observedFocusThreadId,
            observedFocusRelationship,
            focusRecovery.Attempted,
            focusRecovery.ValidationSucceeded,
            focusRecovery.AttachAttempted,
            focusRecovery.AttachSucceeded,
            focusRecovery.SetFocusAttempted,
            focusRecovery.SetFocusPreviousWindow,
            focusRecovery.DetachAttempted,
            focusRecovery.DetachSucceeded);
    }

    private static uint GetWindowIdentity(HWND window, out uint processId)
    {
        if (window == HWND.Null || !PInvoke.IsWindow(window))
        {
            processId = 0;
            return 0;
        }

        return PInvoke.GetWindowThreadProcessId(window, out processId);
    }

    private static ForegroundWindowRelationship GetRelationship(HWND topLevelWindow, HWND candidate)
    {
        if (candidate == HWND.Null)
        {
            return ForegroundWindowRelationship.None;
        }

        if (candidate == topLevelWindow)
        {
            return ForegroundWindowRelationship.SameWindow;
        }

        return PInvoke.IsChild(topLevelWindow, candidate)
            ? ForegroundWindowRelationship.Descendant
            : ForegroundWindowRelationship.Unrelated;
    }

    private static HWND GetFocusedWindow(uint threadId)
    {
        var info = new GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>(),
        };

        return threadId != 0 && PInvoke.GetGUIThreadInfo(threadId, &info)
            ? info.hwndFocus
            : HWND.Null;
    }
}
