using Vok.Core.Clipboard;
using Vok.Core.Workspace;

namespace Vok.Hosting;

public sealed class WorkspacePasteTargetClaim :
    IClipboardImageFileCreationAuthorization,
    IDisposable
{
    private const int Active = 0;
    private const int FileCreationAuthorized = 1;
    private const int Invalidated = 2;
    private int _state;

    internal WorkspacePasteTargetClaim(long generation, ForegroundTarget target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        Generation = generation;
        Target = target;
    }

    internal long Generation { get; }

    public ForegroundTarget Target { get; }

    internal bool IsFileCreationAuthorized =>
        Volatile.Read(ref _state) == FileCreationAuthorized;

    public bool TryClaimFileCreation() =>
        Interlocked.CompareExchange(
            ref _state,
            FileCreationAuthorized,
            Active) == Active;

    internal void Invalidate() =>
        _ = Interlocked.CompareExchange(ref _state, Invalidated, Active);

    public void Dispose() => Invalidate();
}
