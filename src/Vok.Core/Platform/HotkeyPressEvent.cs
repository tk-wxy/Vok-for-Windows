using Vok.Core.Workspace;

namespace Vok.Core.Platform;

public abstract record HotkeyPressEvent(
    long PressGeneration,
    long NativeTimestamp);

public sealed record HotkeyPressStarted(
    long PressGeneration,
    ForegroundTarget ForegroundTarget,
    long NativeTimestamp)
    : HotkeyPressEvent(PressGeneration, NativeTimestamp);

public sealed record HotkeyPressReleased(
    long PressGeneration,
    TimeSpan HeldDuration,
    long NativeTimestamp)
    : HotkeyPressEvent(PressGeneration, NativeTimestamp);

public sealed record HotkeyPressCanceled(
    long PressGeneration,
    long NativeTimestamp)
    : HotkeyPressEvent(PressGeneration, NativeTimestamp);
