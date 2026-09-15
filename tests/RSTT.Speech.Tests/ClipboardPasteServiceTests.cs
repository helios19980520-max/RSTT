using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Input;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class ClipboardPasteServiceTests
{
    [Fact]
    public async Task WholeUnicodeBatchUsesOnePasteCommand()
    {
        var api = new FakeApi();
        using var service = new Win32ClipboardPasteService(api);
        var text = "A whole paragraph. 日本語 🙂 " + new string('W', 8000);
        var result = await service.InjectAsync(Request(text));
        Assert.True(result.Succeeded);
        Assert.Equal(text, api.Text);
        Assert.Equal(1, api.PasteCalls);
        Assert.Equal(4, result.SentInputCount);
    }

    [Fact]
    public async Task BusyClipboardDoesNotSendAnyKeys()
    {
        var api = new FakeApi { ClipboardAvailable = false };
        using var service = new Win32ClipboardPasteService(api);
        Assert.False((await service.InjectAsync(Request("Retain this"))).Succeeded);
        Assert.Equal(0, api.PasteCalls);
    }

    [Fact]
    public async Task FocusChangeWhileShortcutIsHeldCancelsPaste()
    {
        var api = new FakeApi { Held = true };
        using var service = new Win32ClipboardPasteService(api);
        var paste = service.InjectAsync(Request("Retain this"));
        Assert.Equal(0, api.PasteCalls);
        api.Window = 999;
        var result = await paste;
        Assert.Equal(TextInjectionStatus.TargetChanged, result.Status);
        Assert.Equal(0, api.PasteCalls);
    }

    [Fact]
    public async Task ShortcutMustBeReleasedBeforePaste()
    {
        var api = new FakeApi { Held = true };
        using var service = new Win32ClipboardPasteService(api);
        var paste = service.InjectAsync(Request("Paste once"));
        Assert.Equal(0, api.PasteCalls);
        api.Held = false;
        Assert.True((await paste).Succeeded);
        Assert.Equal(1, api.PasteCalls);
    }

    [Fact]
    public async Task BlockedPasteIsNotReportedAsConsumed()
    {
        var api = new FakeApi { AcceptedInputs = 0 };
        using var service = new Win32ClipboardPasteService(api);
        Assert.False((await service.InjectAsync(Request("Retain this"))).Succeeded);
        Assert.Equal(1, api.PasteCalls);
    }

    private static InjectionRequest Request(string text) => new(new SessionGenerationId(1), 1, 1, text, DateTimeOffset.UtcNow);
    private sealed class FakeApi : IClipboardPasteApi
    {
        public nint Window { get; set; } = 123;
        public nint ForegroundWindow => Window;
        public uint GetProcessId(nint window) => uint.MaxValue;
        public bool Held { get; set; }
        public bool AnyKeyDown => Held;
        public bool ClipboardAvailable { get; set; } = true;
        public string? Text { get; private set; }
        public int PasteCalls { get; private set; }
        public int AcceptedInputs { get; set; } = 4;
        public bool TrySetClipboardText(string text) { Text = text; return ClipboardAvailable; }
        public int Paste() { PasteCalls++; return AcceptedInputs; }
    }
}
