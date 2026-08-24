using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Core.Diagnostics;

/// <summary>
/// Immutable evidence for one generation's hide/restore attempt. Timestamps use
/// <see cref="Stopwatch.Frequency"/> and remain zero when the stage was not reached.
/// </summary>
public sealed record WorkspaceRestoreDiagnosticSnapshot(
    long Generation,
    WorkspaceHideReason HideReason,
    ForegroundTarget Target,
    long HideRequestedTimestamp,
    long WindowHiddenTimestamp,
    long TopmostExitAttemptedTimestamp,
    long TopmostExitCompletedTimestamp,
    bool TopmostExitSucceeded,
    long RestoreCalledTimestamp,
    long RestoreReturnedTimestamp,
    bool RestoreFaulted,
    ForegroundRestoreResult Result)
{
    public static WorkspaceRestoreDiagnosticSnapshot None { get; } = new(
        0,
        WorkspaceHideReason.UserAction,
        ForegroundTarget.None,
        0,
        0,
        0,
        0,
        false,
        0,
        0,
        false,
        default);
}
