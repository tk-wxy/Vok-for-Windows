using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Hosting;

public readonly record struct WorkspaceActionHideResult(
    bool Hidden,
    ForegroundTarget Target,
    ForegroundRestoreResult RestoreResult)
{
    public bool CanInjectInput => Hidden && RestoreResult.Succeeded;

    public static WorkspaceActionHideResult NotHidden => new(
        false,
        ForegroundTarget.None,
        new ForegroundRestoreResult(ForegroundRestoreStatus.InvalidTarget, 0));
}
