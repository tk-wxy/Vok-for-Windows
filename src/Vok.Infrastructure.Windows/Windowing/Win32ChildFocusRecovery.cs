using System.Runtime.InteropServices;
using Vok.Core.Workspace;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Windowing;

internal readonly record struct ChildFocusRecoveryResult(
    bool Attempted,
    bool ValidationSucceeded,
    bool AttachAttempted,
    bool AttachSucceeded,
    bool SetFocusAttempted,
    nint SetFocusPreviousWindow,
    bool DetachAttempted,
    bool DetachSucceeded);

internal interface IChildFocusRecovery
{
    ChildFocusRecoveryResult TryRestore(ForegroundTarget target);
}

internal sealed class Win32ChildFocusRecovery(IChildFocusRecoveryNativeApi nativeApi)
    : IChildFocusRecovery
{
    public ChildFocusRecoveryResult TryRestore(ForegroundTarget target)
    {
        if (!IsStillOwnedTarget(target))
        {
            return new ChildFocusRecoveryResult(Attempted: true, ValidationSucceeded: false, false, false, false, 0, false, false);
        }

        var currentThreadId = nativeApi.GetCurrentThreadId();
        if (currentThreadId == target.ThreadId)
        {
            var previous = nativeApi.SetFocus(target.FocusedWindowHandle);
            return new ChildFocusRecoveryResult(
                Attempted: true,
                ValidationSucceeded: true,
                AttachAttempted: false,
                AttachSucceeded: false,
                SetFocusAttempted: true,
                SetFocusPreviousWindow: previous,
                DetachAttempted: false,
                DetachSucceeded: false);
        }

        var attached = nativeApi.AttachThreadInput(currentThreadId, target.ThreadId, attach: true);
        if (!attached)
        {
            return new ChildFocusRecoveryResult(
                Attempted: true,
                ValidationSucceeded: true,
                AttachAttempted: true,
                AttachSucceeded: false,
                SetFocusAttempted: false,
                SetFocusPreviousWindow: 0,
                DetachAttempted: false,
                DetachSucceeded: false);
        }

        nint previousFocus = 0;
        var setFocusAttempted = false;
        var detachSucceeded = false;
        try
        {
            setFocusAttempted = true;
            previousFocus = nativeApi.SetFocus(target.FocusedWindowHandle);
        }
        finally
        {
            detachSucceeded = nativeApi.AttachThreadInput(
                currentThreadId,
                target.ThreadId,
                attach: false);
        }

        return new ChildFocusRecoveryResult(
            Attempted: true,
            ValidationSucceeded: true,
            AttachAttempted: true,
            AttachSucceeded: true,
            SetFocusAttempted: setFocusAttempted,
            SetFocusPreviousWindow: previousFocus,
            DetachAttempted: true,
            DetachSucceeded: detachSucceeded);
    }

    private bool IsStillOwnedTarget(ForegroundTarget target)
    {
        if (target.WindowHandle == 0 ||
            target.FocusedWindowHandle == 0 ||
            !nativeApi.IsWindow(target.WindowHandle) ||
            !nativeApi.IsWindow(target.FocusedWindowHandle) ||
            target.FocusedWindowRelationship is not (
                ForegroundWindowRelationship.SameWindow or
                ForegroundWindowRelationship.Descendant) ||
            nativeApi.GetForegroundWindow() != target.WindowHandle)
        {
            return false;
        }

        var targetThreadId = nativeApi.GetWindowThreadProcessId(
            target.WindowHandle,
            out var targetProcessId);
        var focusThreadId = nativeApi.GetWindowThreadProcessId(
            target.FocusedWindowHandle,
            out var focusProcessId);
        var relationshipValid = target.FocusedWindowHandle == target.WindowHandle ||
            nativeApi.IsChild(target.WindowHandle, target.FocusedWindowHandle);
        return targetProcessId == target.ProcessId &&
               targetThreadId == target.ThreadId &&
               target.FocusedProcessId == target.ProcessId &&
               target.FocusedThreadId == target.ThreadId &&
               focusProcessId == target.FocusedProcessId &&
               focusThreadId == target.FocusedThreadId &&
               relationshipValid;
    }
}

internal interface IChildFocusRecoveryNativeApi
{
    uint GetCurrentThreadId();

    bool IsWindow(nint window);

    uint GetWindowThreadProcessId(nint window, out uint processId);

    bool IsChild(nint parent, nint child);

    nint GetForegroundWindow();

    bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    nint SetFocus(nint window);
}

internal sealed unsafe class Win32ChildFocusRecoveryNativeApi : IChildFocusRecoveryNativeApi
{
    public uint GetCurrentThreadId() => PInvoke.GetCurrentThreadId();

    public bool IsWindow(nint window) => PInvoke.IsWindow(new HWND((void*)window));

    public uint GetWindowThreadProcessId(nint window, out uint processId) =>
        PInvoke.GetWindowThreadProcessId(new HWND((void*)window), out processId);

    public bool IsChild(nint parent, nint child) =>
        PInvoke.IsChild(new HWND((void*)parent), new HWND((void*)child));

    public nint GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach) =>
        PInvoke.AttachThreadInput(sourceThreadId, targetThreadId, attach);

    public nint SetFocus(nint window) =>
        (nint)PInvoke.SetFocus(new HWND((void*)window)).Value;
}
