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
    [InlineData("K")]
    public void InvalidRecordingsCannotBeSaved(string value) =>
        Assert.False(HotkeyGestureParser.TryParse(value, out _, out _));
}
