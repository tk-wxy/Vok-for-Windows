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

public sealed record GlobalHotkeyGesture
{
    private const GlobalHotkeyModifiers SupportedModifiers =
        GlobalHotkeyModifiers.Alt |
        GlobalHotkeyModifiers.Control |
        GlobalHotkeyModifiers.Shift |
        GlobalHotkeyModifiers.Windows;

    public GlobalHotkeyGesture(
        GlobalHotkeyModifiers modifiers,
        uint virtualKey)
    {
        if (!IsValid(modifiers, virtualKey))
        {
            throw new ArgumentException("The global hotkey gesture is not supported.");
        }

        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public static GlobalHotkeyGesture Default { get; } = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt,
        0x20);

    public GlobalHotkeyModifiers Modifiers { get; }

    public uint VirtualKey { get; }

    public string DisplayName => GlobalHotkeyGestureFormatter.Format(this);

    public static bool TryCreate(
        GlobalHotkeyModifiers modifiers,
        uint virtualKey,
        out GlobalHotkeyGesture? gesture)
    {
        if (!IsValid(modifiers, virtualKey))
        {
            gesture = null;
            return false;
        }

        gesture = new GlobalHotkeyGesture(modifiers, virtualKey);
        return true;
    }

    public static bool IsValid(
        GlobalHotkeyModifiers modifiers,
        uint virtualKey)
    {
        if (modifiers == GlobalHotkeyModifiers.None ||
            (modifiers & ~SupportedModifiers) != 0 ||
            virtualKey is 0 or > 0xFF)
        {
            return false;
        }

        // Modifier-only gestures cannot be registered, and F12 is reserved by Windows.
        return virtualKey is not (
            0x10 or 0x11 or 0x12 or
            0x5B or 0x5C or
            0x7B or
            0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);
    }
}
