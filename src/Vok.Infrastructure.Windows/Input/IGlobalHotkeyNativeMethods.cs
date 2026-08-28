using Vok.Core.Platform;

namespace Vok.Infrastructure.Windows.Input;

internal readonly record struct HotkeyThreadMessage(
    int Result,
    uint Message,
    nuint WParam);

internal interface IGlobalHotkeyNativeMethods
{
    uint HotkeyMessage { get; }

    uint TimerMessage { get; }

    uint GetCurrentThreadId();

    void EnsureMessageQueue();

    bool RegisterHotkey(int id, GlobalHotkeyGesture gesture);

    bool UnregisterHotkey(int id);

    nuint StartReleaseTimer(uint intervalMilliseconds);

    bool StopReleaseTimer(nuint timerId);

    bool IsKeyDown(uint virtualKey);

    HotkeyThreadMessage GetMessage();

    bool PostCommand(uint threadId, uint message, nuint commandId);

    bool PostQuit(uint threadId);

    int GetLastError();
}
