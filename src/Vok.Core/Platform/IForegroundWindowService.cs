using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public interface IForegroundWindowService
{
    ForegroundTarget Capture();

    ForegroundRestoreResult Restore(ForegroundTarget target);
}
