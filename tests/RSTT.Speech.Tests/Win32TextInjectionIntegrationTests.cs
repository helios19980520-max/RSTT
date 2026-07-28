using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Input;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class Win32TextInjectionIntegrationTests
{
    [Fact]
    public void NativeInputLayoutMatchesWindowsX64Abi()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(40, Win32TextInjectionService.NativeInputSize);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task UnicodeSendInputTypesIntoNotepad()
    {
        var existingHandles = GetVisibleNotepadHandles();
        if (existingHandles.Count > 0)
        {
            // Never focus, type into, or close a user-owned Notepad window.
            // The native ABI test above remains deterministic in this environment.
            return;
        }

        var testDocument = Path.Combine(Path.GetTempPath(), $"rstt-sendinput-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(testDocument, string.Empty);
        var startInfo = new ProcessStartInfo("notepad.exe")
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(testDocument);
        using var launcher = Process.Start(startInfo);
        Assert.NotNull(launcher);
        var handle = IntPtr.Zero;
        try
        {
            // Windows 11 Notepad is a packaged, multi-process app. Process.Start
            // returns a short-lived launcher rather than the process that owns the
            // editor window, so discover the new top-level window explicitly.
            handle = await WaitForNewNotepadWindowAsync(
                existingHandles,
                Path.GetFileName(testDocument),
                TimeSpan.FromSeconds(10));
            Assert.NotEqual(IntPtr.Zero, handle);
            var focused = await FocusNotepadEditorAsync(handle, TimeSpan.FromSeconds(3));
            if (!focused)
            {
                // An elevated/always-on-top foreground app can prevent a normal
                // test host from activating Notepad. Do not send input to the
                // wrong user window; ABI coverage still runs independently.
                return;
            }

            using var service = new Win32TextInjectionService(NullLogger<Win32TextInjectionService>.Instance);
            const string expected = "RSTT SendInput smoke \u2713";
            foreach (var segment in new[] { "RSTT", " SendInput", " smoke", " \u2713" })
            {
                var result = await service.InjectTextAsync(segment);
                Assert.True(result.Succeeded, result.Message);
            }

            var actual = await WaitForDocumentTextAsync(handle, expected, TimeSpan.FromSeconds(5));
            Assert.Contains(expected, actual, StringComparison.Ordinal);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                await CloseNotepadWithoutSavingAsync(handle);
            }

            File.Delete(testDocument);
        }
    }

    private static HashSet<nint> GetVisibleNotepadHandles()
    {
        var handles = new HashSet<nint>();
        foreach (var process in Process.GetProcessesByName("Notepad"))
        {
            using (process)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    handles.Add(process.MainWindowHandle);
                }
            }
        }

        return handles;
    }

    private static async Task<nint> WaitForNewNotepadWindowAsync(
        HashSet<nint> existingHandles,
        string expectedTitle,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName("Notepad"))
            {
                using (process)
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero &&
                        !existingHandles.Contains(process.MainWindowHandle) &&
                        process.MainWindowTitle.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        return process.MainWindowHandle;
                    }
                }
            }

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    private static async Task<bool> FocusNotepadEditorAsync(nint windowHandle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var currentThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(windowHandle, out _);
            var attachedForeground = foregroundThread != 0 &&
                foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 &&
                targetThread != currentThread &&
                AttachThreadInput(currentThread, targetThread, true);
            try
            {
                KeybdEvent(VkMenu, 0, 0, UIntPtr.Zero);
                KeybdEvent(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
                _ = ShowWindow(windowHandle, SwRestore);
                _ = BringWindowToTop(windowHandle);
                _ = SetForegroundWindow(windowHandle);
                SwitchToThisWindow(windowHandle, true);
                if (GetWindowRect(windowHandle, out var bounds))
                {
                    _ = SetCursorPos(bounds.Left + ((bounds.Right - bounds.Left) / 2), bounds.Top + 16);
                    MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                }
            }
            finally
            {
                if (attachedTarget)
                {
                    _ = AttachThreadInput(currentThread, targetThread, false);
                }
                if (attachedForeground)
                {
                    _ = AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            _ = GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcessId);
            _ = GetWindowThreadProcessId(windowHandle, out var targetProcessId);
            if (foregroundProcessId != 0 && foregroundProcessId == targetProcessId)
            {
                await Task.Delay(200);
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<string> WaitForDocumentTextAsync(
        nint windowHandle,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var window = AutomationElement.FromHandle(windowHandle);
                var document = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                if (document is not null &&
                    document.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                {
                    text = ((TextPattern)pattern).DocumentRange.GetText(-1);
                    if (text.Contains(expected, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
            }
            catch (COMException)
            {
                // Retry while packaged Notepad completes its XAML initialization.
            }

            await Task.Delay(100);
        }

        return text;
    }

    private static async Task CloseNotepadWithoutSavingAsync(nint windowHandle)
    {
        _ = PostMessage(windowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!GetVisibleNotepadHandles().Contains(windowHandle))
            {
                return;
            }

            try
            {
                var discardButton = AutomationElement.RootElement.FindFirst(
                    TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "Don't save"),
                        new PropertyCondition(AutomationElement.NameProperty, "Don’t save"),
                        new PropertyCondition(AutomationElement.NameProperty, "Don't Save")));
                if (discardButton is not null &&
                    discardButton.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return;
                }
            }
            catch (COMException)
            {
                // Packaged Notepad can transiently return RPC_E_SERVERFAULT while
                // replacing the editor window with its save-confirmation dialog.
            }

            await Task.Delay(100);
        }
    }

    private const uint WmClose = 0x0010;
    private const byte VkMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint windowHandle, bool altTab);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out RECT bounds);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
