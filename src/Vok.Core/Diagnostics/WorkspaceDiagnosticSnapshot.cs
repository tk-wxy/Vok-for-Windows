using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Core.Diagnostics;

[Flags]
public enum WorkspaceDiagnosticFlags
{
    None = 0,
    Visible = 1 << 0,
    Topmost = 1 << 1,
    Foreground = 1 << 2,
    Win32KeyboardFocus = 1 << 3,
    WpfInputAnchorFocus = 1 << 4,
    Presented = 1 << 5,
}

/// <summary>
/// A structured, generation-scoped diagnostic view of the production host.
/// Stopwatch timestamps share <see cref="Stopwatch.Frequency"/> and are zero when unavailable.
/// </summary>
public sealed record WorkspaceDiagnosticSnapshot(
    long Generation,
    WorkspaceState State,
    WorkspaceDiagnosticFlags Flags,
    long HotkeyReceivedTimestamp,
    long FirstFrameTimestamp,
    long InputReadyTimestamp,
    long LastEndedGeneration,
    long LastHideRequestedTimestamp,
    long LastRestoreCompletedTimestamp,
    ForegroundRestoreStatus LastRestoreStatus,
    ForegroundTarget CurrentTarget,
    WorkspaceRestoreDiagnosticSnapshot Restore)
{
    public bool IsVisible => (Flags & WorkspaceDiagnosticFlags.Visible) != 0;

    public bool IsTopmost => (Flags & WorkspaceDiagnosticFlags.Topmost) != 0;

    public bool IsForeground => (Flags & WorkspaceDiagnosticFlags.Foreground) != 0;

    public bool HasWin32KeyboardFocus =>
        (Flags & WorkspaceDiagnosticFlags.Win32KeyboardFocus) != 0;

    public bool HasWpfInputAnchorFocus =>
        (Flags & WorkspaceDiagnosticFlags.WpfInputAnchorFocus) != 0;

    public bool IsPresented =>
        (Flags & WorkspaceDiagnosticFlags.Presented) != 0;
}
