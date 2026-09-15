using System.Globalization;

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

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, bool MiddleMouse = false)
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

        if (MiddleMouse) parts.Add("MMB");
        if (VirtualKey != 0) parts.Add(HotkeyGestureParser.FormatVirtualKey(VirtualKey));
        return string.Join('+', parts);
    }
}

public static class HotkeyGestureParser
{
    public static bool TryParse(string? value, out HotkeyGesture gesture, out string error)
    {
        gesture = default!;
        error = string.Empty;
        var text = value?.Trim() ?? string.Empty;
        // '+' separates modifiers, but may also name the main keyboard's plus
        // key. Canonical output uses "Plus"; numpad addition is "NumPadAdd".
        if (text.EndsWith('+'))
        {
            var prefix = text[..^1].TrimEnd();
            if (prefix.Length == 0 || prefix.EndsWith('+')) text = prefix + "Plus";
        }
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
        {
            error = "Choose a key, such as K, 9, [, or NumPadAdd. Modifiers are optional.";
            return false;
        }

        var modifiers = HotkeyModifiers.NoRepeat;
        uint virtualKey = 0;
        var middleMouse = false;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "MMB":
                case "MIDDLEMOUSE":
                    if (middleMouse) { error = "The middle mouse button is repeated."; return false; }
                    middleMouse = true;
                    break;
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

        if (!middleMouse && virtualKey == 0)
        {
            error = "Choose a letter, digit, punctuation, numpad, function, or navigation key. Modifiers are optional.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, middleMouse);
        return true;
    }

    internal static string FormatVirtualKey(uint virtualKey) =>
        virtualKey switch
        {
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x60 and <= 0x69 => $"NumPad{virtualKey - 0x60}",
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
            0x6A => "NumPadMultiply",
            0x6B => "NumPadAdd",
            0x6C => "NumPadSeparator",
            0x6D => "NumPadSubtract",
            0x6E => "NumPadDecimal",
            0x6F => "NumPadDivide",
            0xBA => ";",
            0xBB => "Plus",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            _ => $"VK_{virtualKey:X2}",
        };

    private static bool TryParseVirtualKey(string value, out uint virtualKey)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.StartsWith("VK_", StringComparison.Ordinal))
        {
            return uint.TryParse(normalized.AsSpan(3), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out virtualKey) &&
                (virtualKey is 0xDF or 0xE2 ||
                 !FormatVirtualKey(virtualKey).StartsWith("VK_", StringComparison.Ordinal));
        }
        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]))
        {
            virtualKey = normalized[0];
            return true;
        }

        if (normalized.Length == 7 && normalized.StartsWith("NUMPAD", StringComparison.Ordinal) &&
            char.IsAsciiDigit(normalized[6]))
        {
            virtualKey = (uint)(0x60 + normalized[6] - '0');
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
            "NUMPADMULTIPLY" or "MULTIPLY" => 0x6A,
            "NUMPADADD" or "ADD" => 0x6B,
            "NUMPADSEPARATOR" or "SEPARATOR" => 0x6C,
            "NUMPADSUBTRACT" or "SUBTRACT" => 0x6D,
            "NUMPADDECIMAL" or "DECIMAL" => 0x6E,
            "NUMPADDIVIDE" or "DIVIDE" => 0x6F,
            ";" => 0xBA,
            "PLUS" or "=" => 0xBB,
            "," => 0xBC,
            "-" => 0xBD,
            "." => 0xBE,
            "/" => 0xBF,
            "`" => 0xC0,
            "[" => 0xDB,
            "\\" => 0xDC,
            "]" => 0xDD,
            "'" => 0xDE,
            "SPACE" => 0x20,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "BACKSPACE" => 0x08,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
