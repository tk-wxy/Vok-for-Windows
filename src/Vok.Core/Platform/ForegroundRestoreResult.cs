using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public enum ForegroundRestoreStatus
{
    Confirmed,
    AlreadyForeground,
    ForegroundConfirmedFocusChanged,
    InvalidTarget,
    RequestDenied,
    DifferentForeground,
    FocusRecoveryDetachFailed,
}

public readonly record struct ForegroundRestoreResult(
    ForegroundRestoreStatus Status,
    nint ObservedForeground,
    nint ObservedKeyboardFocus = 0,
    bool SetForegroundAttempted = false,
    bool SetForegroundSucceeded = false,
    bool TargetWindowValid = false,
    bool TargetIdentityMatches = false,
    uint TargetProcessId = 0,
    uint TargetThreadId = 0,
    bool CapturedFocusWindowValid = false,
    bool CapturedFocusIdentityMatches = false,
    uint CapturedFocusProcessId = 0,
    uint CapturedFocusThreadId = 0,
    ForegroundWindowRelationship CapturedFocusRelationship = ForegroundWindowRelationship.None,
    uint ObservedFocusProcessId = 0,
    uint ObservedFocusThreadId = 0,
    ForegroundWindowRelationship ObservedFocusRelationship = ForegroundWindowRelationship.None,
    bool FocusRecoveryAttempted = false,
    bool FocusRecoveryValidationSucceeded = false,
    bool InputQueuesAttachAttempted = false,
    bool InputQueuesAttachSucceeded = false,
    bool SetFocusAttempted = false,
    nint SetFocusPreviousWindow = 0,
    bool InputQueuesDetachAttempted = false,
    bool InputQueuesDetachSucceeded = false)
{
    public bool Succeeded =>
        Status is ForegroundRestoreStatus.Confirmed or ForegroundRestoreStatus.AlreadyForeground;
}
