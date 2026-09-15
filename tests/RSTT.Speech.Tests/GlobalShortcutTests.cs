using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Reflection;
using System.Windows.Markup;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.App.Services;
using Xunit;

namespace RSTT.Speech.Tests;

[Collection(NativeInputTestGroup.Name)]
public sealed class GlobalShortcutTests
{
    private static readonly (string Recorded, string Saved)[] RecordedGestures =
    [
        ("Ctrl+Alt+Shift+F22", "Ctrl+Alt+Shift+F22"),
        ("Ctrl+MMB", "Ctrl+MMB"),
        ("MMB+B", "MMB+B"),
        ("K", "K"),
        ("9", "9"),
        ("[", "["),
        ("VK_DC", "\\"),
        ("VK_6B", "NumPadAdd"),
        ("Ctrl+VK_6B", "Ctrl+NumPadAdd"),
    ];
    [Fact]
    public Task ConfirmClosesTheRealRecorderWithoutClosingAgainOnDeactivation() => RunSta(() =>
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = new DirectoryInfo(TestRepository.AppWorkerRoot);
        while (!File.Exists(Path.Combine(root.FullName, "RSTT.sln"))) root = root.Parent!;
        var source = XDocument.Load(Path.Combine(root.FullName, "src/RSTT.App/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "infra", "clr-namespace:RSTT.App.Infrastructure;assembly=RSTT.App"),
            source.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString().Replace(
            "clr-namespace:RSTT.App.Infrastructure\"", "clr-namespace:RSTT.App.Infrastructure;assembly=RSTT.App\"", StringComparison.Ordinal));
        var owner = new Window { Left = -10000, Top = -10000, Width = 100, Height = 100, ShowInTaskbar = false, ShowActivated = false };
        using var shortcuts = new GlobalHotkeyManager(owner, NullLogger<GlobalHotkeyManager>.Instance);
        _ = new WindowInteropHelper(owner).EnsureHandle();
        try
        {
            foreach (var gesture in RecordedGestures)
            {
                var dialog = new RSTT.App.ShortcutRecorderWindow(shortcuts, RsttHotkey.PasteRecognizedSentences)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000,
                };
                // WM_ACTIVATE during Confirm's Close caused the reported native
                // callback exception. Exercise that same reentrant event order.
                dialog.Closing += (_, _) => typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, [EventArgs.Empty]);
                dialog.Loaded += (_, _) =>
                {
                    typeof(RSTT.App.ShortcutRecorderWindow).GetMethod("OnRecorded", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(dialog, [null, gesture.Recorded]);
                    ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                Assert.True(dialog.ShowDialog());
                Assert.Equal(gesture.Saved, shortcuts.GetGesture(RsttHotkey.PasteRecognizedSentences));
                Assert.False(shortcuts.IsRecording);
            }
        }
        finally { owner.Close(); app.Shutdown(); }
    });

    [Fact]
    public Task ReplacementsConflictsAndClearingUseRealWindowsRegistrations() => RunSta(() =>
    {
        var firstWindow = new Window();
        var secondWindow = new Window();
        using var first = new GlobalHotkeyManager(firstWindow, NullLogger<GlobalHotkeyManager>.Instance);
        using var second = new GlobalHotkeyManager(secondWindow, NullLogger<GlobalHotkeyManager>.Instance);
        _ = new WindowInteropHelper(firstWindow).EnsureHandle();
        _ = new WindowInteropHelper(secondWindow).EnsureHandle();
        try
        {
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "Ctrl+Alt+Shift+F20", out var error), error);
            Assert.False(second.TryReplace(RsttHotkey.ToggleCaptionOverlay, "Ctrl+Alt+Shift+F20", out error));
            Assert.Contains("another application", error);
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "Ctrl+Alt+Shift+F21", out error), error);
            Assert.True(second.TryReplace(RsttHotkey.ToggleCaptionOverlay, "Ctrl+Alt+Shift+F20", out error), error);
            Assert.False(first.TryReplace(RsttHotkey.PasteRecognizedSentences, "Ctrl+Alt+Shift+F21", out error));
            Assert.Contains("already assigned", error);
            Assert.False(first.TryReplace(RsttHotkey.ToggleListening, "Ctrl", out _));
            Assert.Equal("Ctrl+Alt+Shift+F21", first.GetGesture(RsttHotkey.ToggleListening));
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "", out _));
            Assert.Empty(first.GetGesture(RsttHotkey.ToggleListening));
            Assert.True(second.TryReplace(RsttHotkey.ToggleCaptionOverlay, "Ctrl+Alt+Shift+F21", out error), error);
        }
        finally { firstWindow.Close(); secondWindow.Close(); }
    });

    [Fact]
    public Task SpecialKeyAliasesShareWindowsRegistrationsAndKeepNumpadDistinct() => RunSta(() =>
    {
        var firstWindow = new Window();
        var secondWindow = new Window();
        using var first = new GlobalHotkeyManager(firstWindow, NullLogger<GlobalHotkeyManager>.Instance);
        using var second = new GlobalHotkeyManager(secondWindow, NullLogger<GlobalHotkeyManager>.Instance);
        _ = new WindowInteropHelper(firstWindow).EnsureHandle();
        _ = new WindowInteropHelper(secondWindow).EnsureHandle();
        try
        {
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "VK_6B", out var error), error);
            Assert.Equal("NumPadAdd", first.GetGesture(RsttHotkey.ToggleListening));
            Assert.False(second.TryReplace(RsttHotkey.ToggleListening, "NumPadAdd", out error));
            Assert.Contains("another application", error);
            Assert.False(first.TryReplace(RsttHotkey.PasteRecognizedSentences, "NumPadAdd", out error));
            Assert.Contains("already assigned", error);

            // OEM plus and numpad addition may be assigned at the same time.
            Assert.True(second.TryReplace(RsttHotkey.ToggleListening, "+", out error), error);
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "\\", out error), error);
            Assert.False(second.TryReplace(RsttHotkey.ToggleListening, "VK_DC", out error));
            Assert.Equal("Plus", second.GetGesture(RsttHotkey.ToggleListening));
            Assert.True(first.TryReplace(RsttHotkey.ToggleListening, "", out error), error);
            Assert.True(second.TryReplace(RsttHotkey.ToggleListening, "VK_DC", out error), error);
        }
        finally { firstWindow.Close(); secondWindow.Close(); }
    });

    [Fact]
    public Task MouseChordAndRecordingCancellationPreserveWorkingBinding() => RunSta(() =>
    {
        var window = new Window();
        using var shortcuts = new GlobalHotkeyManager(window, NullLogger<GlobalHotkeyManager>.Instance);
        _ = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            Assert.True(shortcuts.TryReplace(RsttHotkey.PasteRecognizedSentences, "MMB+B", out var error), error);
            Assert.True(shortcuts.BeginRecording(out error), error);
            Assert.True(shortcuts.IsRecording);
            shortcuts.EndRecording();
            Assert.False(shortcuts.IsRecording);
            Assert.Equal("MMB+B", shortcuts.GetGesture(RsttHotkey.PasteRecognizedSentences));
            Assert.True(shortcuts.TryReplace(RsttHotkey.PasteRecognizedSentences,
                GlobalHotkeyManager.GetDefaultGesture(RsttHotkey.PasteRecognizedSentences), out error), error);
            Assert.Equal("MMB", shortcuts.GetGesture(RsttHotkey.PasteRecognizedSentences));
            Assert.True(shortcuts.TryReplace(RsttHotkey.PasteRecognizedSentences, "", out _));
            Assert.Empty(shortcuts.GetGesture(RsttHotkey.PasteRecognizedSentences));
        }
        finally { window.Close(); }
    });

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
