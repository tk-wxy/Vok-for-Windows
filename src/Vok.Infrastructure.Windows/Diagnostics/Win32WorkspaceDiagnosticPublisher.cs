using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vok.Core.Diagnostics;

namespace Vok.Infrastructure.Windows.Diagnostics;

/// <summary>
/// Publishes a low-frequency, cross-process diagnostic contract as HWND properties.
/// Sequence is a seqlock: odd means a write is in progress, equal even reads are coherent.
/// Numeric values are encoded as value + 1 so a missing property remains distinguishable.
/// </summary>
public sealed partial class Win32WorkspaceDiagnosticPublisher : IWorkspaceDiagnosticPublisher
{
    public const string Prefix = "Vok.M0.Diagnostics.";

    private static readonly string[] PropertySuffixes =
    [
        "Sequence",
        "Generation",
        "State",
        "Flags",
        "Frequency",
        "HotkeyReceivedTimestamp",
        "FirstFrameTimestamp",
        "InputReadyTimestamp",
        "LastEndedGeneration",
        "LastHideRequestedTimestamp",
        "LastRestoreCompletedTimestamp",
        "LastRestoreStatus",
        "CurrentTargetWindow",
        "CurrentTargetProcessId",
        "CurrentTargetThreadId",
        "CurrentTargetFocusWindow",
        "CurrentTargetFocusProcessId",
        "CurrentTargetFocusThreadId",
        "CurrentTargetFocusRelationship",
        "RestoreGeneration",
        "RestoreHideReason",
        "RestoreTargetWindow",
        "RestoreTargetProcessId",
        "RestoreTargetThreadId",
        "RestoreTargetFocusWindow",
        "RestoreTargetFocusProcessId",
        "RestoreTargetFocusThreadId",
        "RestoreTargetFocusRelationship",
        "RestoreHideRequestedTimestamp",
        "RestoreWindowHiddenTimestamp",
        "RestoreTopmostExitAttemptedTimestamp",
        "RestoreTopmostExitCompletedTimestamp",
        "RestoreTopmostExitSucceeded",
        "RestoreCalledTimestamp",
        "RestoreReturnedTimestamp",
        "RestoreFaulted",
        "RestoreObservedForeground",
        "RestoreObservedFocus",
        "RestoreSetForegroundAttempted",
        "RestoreSetForegroundSucceeded",
        "RestoreTargetWindowValid",
        "RestoreTargetIdentityMatches",
        "RestoreCurrentTargetProcessId",
        "RestoreCurrentTargetThreadId",
        "RestoreCapturedFocusWindowValid",
        "RestoreCapturedFocusIdentityMatches",
        "RestoreCurrentCapturedFocusProcessId",
        "RestoreCurrentCapturedFocusThreadId",
        "RestoreCurrentCapturedFocusRelationship",
        "RestoreObservedFocusProcessId",
        "RestoreObservedFocusThreadId",
        "RestoreObservedFocusRelationship",
    ];

    private readonly object _gate = new();
    private long _sequence;

    public void Publish(nint workspaceWindow, WorkspaceDiagnosticSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfZero(workspaceWindow);

        lock (_gate)
        {
            var completedSequence = checked(_sequence + 2);
            _sequence = completedSequence;
            Set(workspaceWindow, "Sequence", completedSequence - 1);
            SetEncoded(workspaceWindow, "Generation", snapshot.Generation);
            SetEncoded(workspaceWindow, "State", (long)snapshot.State);
            SetEncoded(workspaceWindow, "Flags", (long)snapshot.Flags);
            SetEncoded(workspaceWindow, "Frequency", Stopwatch.Frequency);
            SetEncoded(workspaceWindow, "HotkeyReceivedTimestamp", snapshot.HotkeyReceivedTimestamp);
            SetEncoded(workspaceWindow, "FirstFrameTimestamp", snapshot.FirstFrameTimestamp);
            SetEncoded(workspaceWindow, "InputReadyTimestamp", snapshot.InputReadyTimestamp);
            SetEncoded(workspaceWindow, "LastEndedGeneration", snapshot.LastEndedGeneration);
            SetEncoded(workspaceWindow, "LastHideRequestedTimestamp", snapshot.LastHideRequestedTimestamp);
            SetEncoded(workspaceWindow, "LastRestoreCompletedTimestamp", snapshot.LastRestoreCompletedTimestamp);
            SetEncoded(workspaceWindow, "LastRestoreStatus", (long)snapshot.LastRestoreStatus);
            SetTarget(workspaceWindow, "CurrentTarget", snapshot.CurrentTarget);

            var restore = snapshot.Restore;
            SetEncoded(workspaceWindow, "RestoreGeneration", restore.Generation);
            SetEncoded(workspaceWindow, "RestoreHideReason", (long)restore.HideReason);
            SetTarget(workspaceWindow, "RestoreTarget", restore.Target);
            SetEncoded(workspaceWindow, "RestoreHideRequestedTimestamp", restore.HideRequestedTimestamp);
            SetEncoded(workspaceWindow, "RestoreWindowHiddenTimestamp", restore.WindowHiddenTimestamp);
            SetEncoded(workspaceWindow, "RestoreTopmostExitAttemptedTimestamp", restore.TopmostExitAttemptedTimestamp);
            SetEncoded(workspaceWindow, "RestoreTopmostExitCompletedTimestamp", restore.TopmostExitCompletedTimestamp);
            SetEncoded(workspaceWindow, "RestoreTopmostExitSucceeded", restore.TopmostExitSucceeded ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreCalledTimestamp", restore.RestoreCalledTimestamp);
            SetEncoded(workspaceWindow, "RestoreReturnedTimestamp", restore.RestoreReturnedTimestamp);
            SetEncoded(workspaceWindow, "RestoreFaulted", restore.RestoreFaulted ? 1 : 0);

            var result = restore.Result;
            SetEncoded(workspaceWindow, "RestoreObservedForeground", result.ObservedForeground);
            SetEncoded(workspaceWindow, "RestoreObservedFocus", result.ObservedKeyboardFocus);
            SetEncoded(workspaceWindow, "RestoreSetForegroundAttempted", result.SetForegroundAttempted ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreSetForegroundSucceeded", result.SetForegroundSucceeded ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreTargetWindowValid", result.TargetWindowValid ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreTargetIdentityMatches", result.TargetIdentityMatches ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreCurrentTargetProcessId", result.TargetProcessId);
            SetEncoded(workspaceWindow, "RestoreCurrentTargetThreadId", result.TargetThreadId);
            SetEncoded(workspaceWindow, "RestoreCapturedFocusWindowValid", result.CapturedFocusWindowValid ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreCapturedFocusIdentityMatches", result.CapturedFocusIdentityMatches ? 1 : 0);
            SetEncoded(workspaceWindow, "RestoreCurrentCapturedFocusProcessId", result.CapturedFocusProcessId);
            SetEncoded(workspaceWindow, "RestoreCurrentCapturedFocusThreadId", result.CapturedFocusThreadId);
            SetEncoded(workspaceWindow, "RestoreCurrentCapturedFocusRelationship", (long)result.CapturedFocusRelationship);
            SetEncoded(workspaceWindow, "RestoreObservedFocusProcessId", result.ObservedFocusProcessId);
            SetEncoded(workspaceWindow, "RestoreObservedFocusThreadId", result.ObservedFocusThreadId);
            SetEncoded(workspaceWindow, "RestoreObservedFocusRelationship", (long)result.ObservedFocusRelationship);
            Set(workspaceWindow, "Sequence", completedSequence);
        }
    }

    public void Clear(nint workspaceWindow)
    {
        if (workspaceWindow == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var suffix in PropertySuffixes)
            {
                _ = RemoveProp(workspaceWindow, Prefix + suffix);
            }
        }
    }

    private static void SetTarget(nint window, string prefix, Vok.Core.Workspace.ForegroundTarget target)
    {
        SetEncoded(window, prefix + "Window", target.WindowHandle);
        SetEncoded(window, prefix + "ProcessId", target.ProcessId);
        SetEncoded(window, prefix + "ThreadId", target.ThreadId);
        SetEncoded(window, prefix + "FocusWindow", target.FocusedWindowHandle);
        SetEncoded(window, prefix + "FocusProcessId", target.FocusedProcessId);
        SetEncoded(window, prefix + "FocusThreadId", target.FocusedThreadId);
        SetEncoded(window, prefix + "FocusRelationship", (long)target.FocusedWindowRelationship);
    }

    private static void SetEncoded(nint window, string suffix, long value) =>
        Set(window, suffix, checked(value + 1));

    private static void Set(nint window, string suffix, long value)
    {
        var name = Prefix + suffix;
        if (!SetProp(window, name, checked((nint)value)))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to publish workspace diagnostic property '{name}'.");
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetPropW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProp(nint window, string name, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RemovePropW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint RemoveProp(nint window, string name);
}
