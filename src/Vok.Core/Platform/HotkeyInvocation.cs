using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public sealed record HotkeyInvocation(
    ForegroundTarget ForegroundTarget,
    long NativeTimestamp);
