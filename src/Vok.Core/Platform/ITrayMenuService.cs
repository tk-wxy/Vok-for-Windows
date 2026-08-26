namespace Vok.Core.Platform;

public interface ITrayMenuService
{
    TrayIconCommand? Show(
        nint ownerWindow,
        Guid iconId,
        bool keyboardInvocation);
}
