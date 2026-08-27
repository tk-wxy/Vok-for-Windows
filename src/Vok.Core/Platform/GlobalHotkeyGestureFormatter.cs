using System.Globalization;

namespace Vok.Core.Platform;

public static class GlobalHotkeyGestureFormatter
{
    public static string Format(GlobalHotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = FormatModifiers(gesture.Modifiers).ToList();
        parts.Add(FormatVirtualKey(gesture.VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string FormatModifiers(
        GlobalHotkeyModifiers modifiers,
        string trailingText = "…")
    {
        var parts = FormatModifiers(modifiers).ToList();
        if (!string.IsNullOrEmpty(trailingText))
        {
            parts.Add(trailingText);
        }

        return string.Join(" + ", parts);
    }

    private static IEnumerable<string> FormatModifiers(GlobalHotkeyModifiers modifiers)
    {
        if ((modifiers & GlobalHotkeyModifiers.Control) != 0)
        {
            yield return "Ctrl";
        }
        if ((modifiers & GlobalHotkeyModifiers.Alt) != 0)
        {
            yield return "Alt";
        }
        if ((modifiers & GlobalHotkeyModifiers.Shift) != 0)
        {
            yield return "Shift";
        }
        if ((modifiers & GlobalHotkeyModifiers.Windows) != 0)
        {
            yield return "Win";
        }
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString(CultureInfo.InvariantCulture);
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return $"Num {virtualKey - 0x60}";
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xBA => ";",
            0xBB => "+",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK 0x{virtualKey:X2}",
        };
    }
}
