using RSTT.Core.Hotkeys;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class ShortcutGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+K", "Ctrl+Alt+Shift+K")]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("MMB", "MMB")]
    [InlineData("MMB+B", "MMB+B")]
    [InlineData("B+Ctrl+MMB", "Ctrl+MMB+B")]
    [InlineData("k", "K")]
    [InlineData("9", "9")]
    [InlineData("[", "[")]
    [InlineData("\\", "\\")]
    [InlineData("VK_DC", "\\")]
    [InlineData("VK_6B", "NumPadAdd")]
    [InlineData("Ctrl+VK_6B", "Ctrl+NumPadAdd")]
    [InlineData("MMB+VK_DB", "MMB+[")]
    [InlineData("Shift+VK_DE", "Shift+'")]
    [InlineData("+", "Plus")]
    [InlineData("Ctrl++", "Ctrl+Plus")]
    [InlineData("Ctrl + +", "Ctrl+Plus")]
    [InlineData("=", "Plus")]
    public void CapturedGesturesRoundTripInCanonicalForm(string value, string expected)
    {
        Assert.True(HotkeyGestureParser.TryParse(value, out var gesture, out var error), error);
        Assert.Equal(expected, gesture.ToString());
        Assert.True(HotkeyGestureParser.TryParse(expected, out var again, out error), error);
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("MMB+MMB")]
    [InlineData("MMB+A+B")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+")]
    [InlineData("K+9")]
    [InlineData("Ctrl++K")]
    [InlineData("VK_00")]
    [InlineData("VK_10")]
    [InlineData("VK_FF")]
    [InlineData("VK_GG")]
    public void InvalidRecordingsCannotBeSaved(string value) =>
        Assert.False(HotkeyGestureParser.TryParse(value, out _, out _));

    [Theory]
    [InlineData(0x39u, "9")]
    [InlineData(0x4Bu, "K")]
    [InlineData(0x60u, "NumPad0")]
    [InlineData(0x69u, "NumPad9")]
    [InlineData(0x6Au, "NumPadMultiply")]
    [InlineData(0x6Bu, "NumPadAdd")]
    [InlineData(0x6Cu, "NumPadSeparator")]
    [InlineData(0x6Du, "NumPadSubtract")]
    [InlineData(0x6Eu, "NumPadDecimal")]
    [InlineData(0x6Fu, "NumPadDivide")]
    [InlineData(0xBAu, ";")]
    [InlineData(0xBBu, "Plus")]
    [InlineData(0xBCu, ",")]
    [InlineData(0xBDu, "-")]
    [InlineData(0xBEu, ".")]
    [InlineData(0xBFu, "/")]
    [InlineData(0xC0u, "`")]
    [InlineData(0xDBu, "[")]
    [InlineData(0xDCu, "\\")]
    [InlineData(0xDDu, "]")]
    [InlineData(0xDEu, "'")]
    [InlineData(0xDFu, "VK_DF")]
    [InlineData(0xE2u, "VK_E2")]
    public void RecorderKeyCodesCanBeConfirmedWithoutChangingThePhysicalKey(uint virtualKey, string label)
    {
        // This is the exact formatter -> parser path used by keyboard capture
        // and Confirm. Preserve NoRepeat even when no modifier is held.
        var recorded = new HotkeyGesture(HotkeyModifiers.NoRepeat, virtualKey);
        Assert.Equal(label, recorded.ToString());
        Assert.True(HotkeyGestureParser.TryParse(recorded.ToString(), out var confirmed, out var error), error);
        Assert.Equal(recorded, confirmed);
    }

    [Theory]
    [InlineData("9", "NumPad9")]
    [InlineData("Plus", "NumPadAdd")]
    [InlineData(".", "NumPadDecimal")]
    public void MainKeyboardAndNumpadKeysRemainSeparateBindings(string mainKey, string numpadKey)
    {
        Assert.True(HotkeyGestureParser.TryParse(mainKey, out var main, out _));
        Assert.True(HotkeyGestureParser.TryParse(numpadKey, out var numpad, out _));
        Assert.NotEqual(main.VirtualKey, numpad.VirtualKey);
    }
}
