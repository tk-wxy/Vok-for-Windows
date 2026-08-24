namespace Vok.Core.Platform;

[Flags]
public enum GlobalHotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record GlobalHotkeyGesture(
    GlobalHotkeyModifiers Modifiers,
    uint VirtualKey,
    string DisplayName)
{
    public static GlobalHotkeyGesture M0DiagnosticDefault { get; } = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt,
        0x20,
        "Ctrl + Alt + Space");
}
