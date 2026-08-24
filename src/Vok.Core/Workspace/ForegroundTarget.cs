namespace Vok.Core.Workspace;

public readonly record struct ForegroundTarget(
    nint WindowHandle,
    uint ProcessId,
    uint ThreadId,
    nint FocusedWindowHandle = 0,
    uint FocusedProcessId = 0,
    uint FocusedThreadId = 0,
    ForegroundWindowRelationship FocusedWindowRelationship = ForegroundWindowRelationship.None)
{
    public bool CanRestore => WindowHandle != 0;

    public static ForegroundTarget None => default;
}
