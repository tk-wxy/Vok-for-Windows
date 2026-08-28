using Vok.Core.Workspace;

namespace Vok.Core.Clipboard;

public interface IPasteInputService
{
    Task<bool> InjectPasteAsync(
        ForegroundTarget expectedTarget,
        CancellationToken cancellationToken = default);
}
