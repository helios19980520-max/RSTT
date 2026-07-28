namespace RSTT.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(HotkeyGestureParser.FormatVirtualKey(VirtualKey));
        return string.Join('+', parts);
    }
}

public static class HotkeyGestureParser
{
    public static bool TryParse(string? value, out HotkeyGesture gesture, out string error)
    {
        gesture = default!;
        error = string.Empty;
        var parts = value?.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length < 2)
        {
            error = "Use at least one modifier plus a key, for example Ctrl+Alt+R.";
            return false;
        }

        var modifiers = HotkeyModifiers.NoRepeat;
        uint virtualKey = 0;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (virtualKey != 0 || !TryParseVirtualKey(part, out virtualKey))
                    {
                        error = $"'{part}' is not a supported shortcut key.";
                        return false;
                    }

                    break;
            }
        }

        if (virtualKey == 0 || (modifiers & ~HotkeyModifiers.NoRepeat) == HotkeyModifiers.None)
        {
            error = "A shortcut needs a modifier and one letter, digit, function key, or navigation key.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    internal static string FormatVirtualKey(uint virtualKey) =>
        virtualKey switch
        {
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            _ => $"VK_{virtualKey:X2}",
        };

    private static bool TryParseVirtualKey(string value, out uint virtualKey)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]))
        {
            virtualKey = normalized[0];
            return true;
        }

        if (normalized.StartsWith('F') &&
            int.TryParse(normalized.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x6F + functionKey);
            return true;
        }

        virtualKey = normalized switch
        {
            "SPACE" => 0x20,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
