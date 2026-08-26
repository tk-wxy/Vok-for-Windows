using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public sealed class TrayIconCommandEventArgs(
    TrayIconCommand command,
    ForegroundTarget foregroundTarget,
    long nativeTimestamp) : EventArgs
{
    public TrayIconCommand Command { get; } = command;

    public ForegroundTarget ForegroundTarget { get; } = foregroundTarget;

    public long NativeTimestamp { get; } = nativeTimestamp;
}
