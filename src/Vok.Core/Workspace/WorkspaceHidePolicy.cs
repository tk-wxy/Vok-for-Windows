namespace Vok.Core.Workspace;

public enum WorkspaceTargetRestoreMode
{
    RestoreCapturedTarget,
    PreserveCurrentForeground,
}

public readonly record struct WorkspaceHidePolicy
{
    private WorkspaceHidePolicy(
        WorkspaceHideReason reason,
        WorkspaceTargetRestoreMode targetRestoreMode)
    {
        Reason = reason;
        TargetRestoreMode = targetRestoreMode;
    }

    public WorkspaceHideReason Reason { get; }

    public WorkspaceTargetRestoreMode TargetRestoreMode { get; }

    public static WorkspaceHidePolicy For(WorkspaceHideReason reason) => reason switch
    {
        WorkspaceHideReason.ForegroundLost =>
            new(reason, WorkspaceTargetRestoreMode.PreserveCurrentForeground),
        WorkspaceHideReason.Hotkey or
        WorkspaceHideReason.Escape or
        WorkspaceHideReason.UserAction or
        WorkspaceHideReason.LauncherAction or
        WorkspaceHideReason.ClipboardPaste or
        WorkspaceHideReason.ApplicationExit or
        WorkspaceHideReason.ShowFailure =>
            new(reason, WorkspaceTargetRestoreMode.RestoreCapturedTarget),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
