using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Settings;
using RSTT.Input;

namespace RSTT.Notepad.IntegrationRunner;

internal static class Program
{
    private const string Sentence =
        "Exact sustained Windows 11 Notepad delivery: café, 日本語, 한국어, 🙂. ";
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
    };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var repetitions = args.Length > 0 &&
            int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, 1, 1_000)
                : 100;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"rstt-notepad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var documentName = $"RSTT-Exact-{Guid.NewGuid():N}.txt";
        var documentPath = Path.Combine(directory, documentName);
        await File.WriteAllTextAsync(documentPath, string.Empty).ConfigureAwait(false);
        var before = EnumerateNotepadWindows().Select(item => item.Handle).ToHashSet();
        nint testWindow = 0;
        Process? launcher = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var startInfo = new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(documentPath);
            launcher = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows Notepad did not start.");
            var window = await WaitForUniqueDocumentWindowAsync(
                    documentName,
                    documentPath,
                    before,
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            testWindow = window.Handle;
            if (!FocusWindow(testWindow, TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    $"The unique Notepad HWND 0x{testWindow:X} could not become foreground.");
            }

            var editor = FindEditor(testWindow)
                ?? throw new InvalidOperationException(
                    "UI Automation could not find the document editor in the unique Notepad window.");
            editor.SetFocus();
            await Task.Delay(300).ConfigureAwait(false);
            using var foregroundKeeper = new ForegroundKeeper(testWindow);

            var settings = new FixedSettingsService();
            using var injection = new Win32TextInjectionService(
                NullLogger<Win32TextInjectionService>.Instance,
                settings);
            var expected = string.Concat(Enumerable.Repeat(Sentence, repetitions));
            var generation = new SessionGenerationId(1);
            for (var index = 1; index <= repetitions; index++)
            {
                var result = await injection.InjectAsync(
                        new InjectionRequest(
                            generation,
                            index,
                            index,
                            Sentence,
                            DateTimeOffset.UtcNow))
                    .ConfigureAwait(false);
                if (result.Status != TextInjectionStatus.Success)
                {
                    throw new InvalidOperationException(
                        $"Commit {index} failed: {result.Status}; expected/sent " +
                        $"{result.ExpectedInputCount}/{result.SentInputCount}; " +
                        $"offset {result.CommittedUtf16Offset}; {result.DiagnosticMessage}");
                }
            }

            var actual = await WaitForTextAsync(editor, expected, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
            SaveFocusedDocument();
            var saved = await WaitForFileTextAsync(documentPath, expected, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.Ordinal) ||
                !string.Equals(expected, saved, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Notepad UIA diverged at UTF-16 offset {FirstDifference(expected, actual)} " +
                    $"({actual.Length} units), saved file at offset {FirstDifference(expected, saved)} " +
                    $"({saved.Length} units). Expected prefix '{DiagnosticPrefix(expected)}'; " +
                    $"UIA prefix '{DiagnosticPrefix(actual)}'; saved prefix '{DiagnosticPrefix(saved)}'.");
            }

            stopwatch.Stop();
            var evidence = new
            {
                Status = "Passed",
                Repetitions = repetitions,
                ExpectedUtf16Units = expected.Length,
                ObservedUtf16Units = actual.Length,
                WindowHandle = $"0x{testWindow:X}",
                window.ProcessId,
                window.ProcessPath,
                window.FileVersion,
                DeliveryMode = TextInjectionDeliveryMode.Compatibility.ToString(),
                BlockUtf16Units = 1,
                InterBlockDelayMilliseconds = 20,
                ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                Document = documentName,
                ExistingNotepadWindows = before.Count,
                Timestamp = DateTimeOffset.UtcNow,
            };
            var reportPath = GetReportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                    reportPath,
                    JsonSerializer.Serialize(evidence, ReportJsonOptions))
                .ConfigureAwait(false);
            Console.WriteLine($"PASS: {repetitions} commits, {expected.Length} UTF-16 units, exact UIA and saved-file equality.");
            Console.WriteLine($"Evidence: {reportPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
        finally
        {
            if (testWindow != 0)
            {
                TrySaveAndClose(testWindow);
            }

            launcher?.Dispose();
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<NotepadWindow> WaitForUniqueDocumentWindowAsync(
        string documentName,
        string documentPath,
        HashSet<nint> existingWindows,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = EnumerateNotepadWindows().FirstOrDefault(window =>
                window.Title.Contains(documentName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (existingWindows.Contains(match.Handle))
                {
                    return await MoveTemporaryTabToFreshWindowAsync(
                            match,
                            documentName,
                            documentPath,
                            existingWindows,
                            timeout)
                        .ConfigureAwait(false);
                }

                return match;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"A unique Notepad window for '{documentName}' was not created.");
    }

    private static async Task<NotepadWindow> MoveTemporaryTabToFreshWindowAsync(
        NotepadWindow reusedWindow,
        string documentName,
        string documentPath,
        HashSet<nint> existingWindows,
        TimeSpan timeout)
    {
        if (!FocusWindow(reusedWindow.Handle, TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(
                "Modern Notepad reused a user-owned window and that temporary test tab could not be focused.");
        }

        System.Windows.Forms.SendKeys.SendWait("^+n");
        System.Windows.Forms.Application.DoEvents();
        var deadline = DateTime.UtcNow + timeout;
        NotepadWindow? freshWindow = null;
        var shortcutDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < shortcutDeadline)
        {
            freshWindow = EnumerateNotepadWindows().FirstOrDefault(window =>
                !existingWindows.Contains(window.Handle) &&
                window.Handle != reusedWindow.Handle);
            if (freshWindow is not null)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        if (freshWindow is null)
        {
            InvokeNotepadNewWindowMenu(reusedWindow.Handle);
            while (DateTime.UtcNow < deadline)
            {
                freshWindow = EnumerateNotepadWindows().FirstOrDefault(window =>
                    !existingWindows.Contains(window.Handle) &&
                    window.Handle != reusedWindow.Handle);
                if (freshWindow is not null)
                {
                    break;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        if (freshWindow is null)
        {
            throw new InvalidOperationException(
                "Notepad reused a user-owned window and its New window command did not create a fresh HWND.");
        }

        // The selected tab in the reused window is the unique, empty test file
        // opened by this runner. Close only that tab before continuing.
        if (!FocusWindow(reusedWindow.Handle, TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(
                "The temporary test tab could not be safely closed in the reused window.");
        }

        System.Windows.Forms.SendKeys.SendWait("^w");
        System.Windows.Forms.Application.DoEvents();
        await Task.Delay(200).ConfigureAwait(false);

        if (!FocusWindow(freshWindow.Handle, TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The fresh Notepad window could not become foreground.");
        }

        FindEditor(freshWindow.Handle)?.SetFocus();
        if (!InvokeNotepadFileMenuItem(freshWindow.Handle, "Open"))
        {
            System.Windows.Forms.SendKeys.SendWait("^o");
            System.Windows.Forms.Application.DoEvents();
        }
        await Task.Delay(1_000).ConfigureAwait(false);
        System.Windows.Forms.SendKeys.SendWait(documentPath);
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        System.Windows.Forms.Application.DoEvents();
        while (DateTime.UtcNow < deadline)
        {
            var opened = EnumerateNotepadWindows().FirstOrDefault(window =>
                window.Handle == freshWindow.Handle &&
                window.Title.Contains(documentName, StringComparison.OrdinalIgnoreCase));
            if (opened is not null)
            {
                return opened;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "The uniquely named test document did not open in the fresh Notepad window.");
    }

    private static void InvokeNotepadNewWindowMenu(nint windowHandle)
    {
        _ = InvokeNotepadFileMenuItem(windowHandle, "New window");
    }

    private static bool InvokeNotepadFileMenuItem(nint windowHandle, string itemName)
    {
        var root = AutomationElement.FromHandle(windowHandle);
        var fileMenu = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.MenuItem),
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    "File")));
        if (fileMenu is null ||
            !fileMenu.TryGetCurrentPattern(InvokePattern.Pattern, out var fileInvoke))
        {
            return false;
        }

        ((InvokePattern)fileInvoke).Invoke();
        Thread.Sleep(150);
        var menuItem = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.MenuItem),
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    itemName)));
        if (menuItem?.TryGetCurrentPattern(InvokePattern.Pattern, out var menuItemInvoke) == true)
        {
            ((InvokePattern)menuItemInvoke).Invoke();
            return true;
        }

        return false;
    }

    private static List<NotepadWindow> EnumerateNotepadWindows()
    {
        var windows = new List<NotepadWindow>();
        _ = EnumWindows(
            (handle, parameter) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }

                var titleLength = GetWindowTextLength(handle);
                if (titleLength == 0)
                {
                    return true;
                }

                var buffer = new char[titleLength + 1];
                _ = GetWindowText(handle, buffer, buffer.Length);
                var title = new string(buffer, 0, titleLength);
                _ = parameter;
                var windowThreadId = GetWindowThreadProcessId(handle, out var processId);
                if (windowThreadId == 0 || processId == 0)
                {
                    return true;
                }
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    var processPath = process.MainModule?.FileName ?? string.Empty;
                    if (!Path.GetFileName(processPath)
                            .Equals("Notepad.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    var version = processPath.Length == 0
                        ? string.Empty
                        : FileVersionInfo.GetVersionInfo(processPath).FileVersion ?? string.Empty;
                    windows.Add(new NotepadWindow(
                        handle,
                        processId,
                        title,
                        processPath,
                        version));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                        InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                }

                return true;
            },
            0);
        return windows;
    }

    private static AutomationElement? FindEditor(nint windowHandle)
    {
        var root = AutomationElement.FromHandle(windowHandle);
        var document = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Document));
        if (document is not null)
        {
            return document;
        }

        return root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Edit));
    }

    private static async Task<string> WaitForTextAsync(
        AutomationElement editor,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var actual = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            actual = ReadEditorText(editor);
            if (string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return actual;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return actual;
    }

    private static string ReadEditorText(AutomationElement editor)
    {
        if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
        {
            return ((ValuePattern)valuePattern).Current.Value;
        }

        if (editor.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
        {
            return ((TextPattern)textPattern).DocumentRange.GetText(-1)
                .TrimEnd('\r', '\n');
        }

        throw new InvalidOperationException(
            "The Notepad editor exposes neither ValuePattern nor TextPattern.");
    }

    private static async Task<string> WaitForFileTextAsync(
        string path,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var actual = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                actual = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                if (string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    return actual;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return actual;
    }

    private static void SaveFocusedDocument()
    {
        System.Windows.Forms.SendKeys.SendWait("^s");
        System.Windows.Forms.Application.DoEvents();
    }

    private static bool FocusWindow(nint handle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var currentThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(handle, out _);
            var attachedForeground = foregroundThread != 0 &&
                foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 &&
                targetThread != currentThread &&
                AttachThreadInput(currentThread, targetThread, true);
            try
            {
                _ = ShowWindow(handle, 9);
                _ = BringWindowToTop(handle);
                _ = SetForegroundWindow(handle);
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

            if (GetForegroundWindow() == handle)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static int FirstDifference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < length; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return length;
    }

    private static string DiagnosticPrefix(string value)
    {
        const int maximumLength = 120;
        var prefix = value[..Math.Min(value.Length, maximumLength)];
        return prefix
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static void TrySaveAndClose(nint handle)
    {
        try
        {
            if (IsWindow(handle))
            {
                _ = SetForegroundWindow(handle);
                SaveFocusedDocument();
                _ = PostMessage(handle, 0x0010, 0, 0);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string GetReportPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RSTT.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "artifacts",
            "test-results",
            "notepad-integration.json");
    }

    private sealed class FixedSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            TextInjectionDeliveryMode = TextInjectionDeliveryMode.Compatibility,
        };

        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed record NotepadWindow(
        nint Handle,
        uint ProcessId,
        string Title,
        string ProcessPath,
        string FileVersion);

    private sealed class ForegroundKeeper : IDisposable
    {
        private readonly nint _windowHandle;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;

        public ForegroundKeeper(nint windowHandle)
        {
            _windowHandle = windowHandle;
            _worker = Task.Run(KeepForegroundAsync);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(item => item is OperationCanceledException))
            {
            }

            _stop.Dispose();
        }

        private async Task KeepForegroundAsync()
        {
            while (!_stop.IsCancellationRequested && IsWindow(_windowHandle))
            {
                if (GetForegroundWindow() != _windowHandle)
                {
                    _ = FocusWindow(_windowHandle, TimeSpan.FromSeconds(1));
                }

                await Task.Delay(10, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, char[] text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint attachThreadId,
        uint attachToThreadId,
        bool attach);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

}
