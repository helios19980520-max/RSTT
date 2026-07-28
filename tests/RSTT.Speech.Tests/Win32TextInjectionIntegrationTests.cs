using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Input;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class Win32TextInjectionIntegrationTests
{
    private static readonly string[] ExactCorpus =
    [
        "The quick brown fox jumps over the lazy dog.",
        "Punctuation: commas, periods... semicolons; colons: quotes \"exact\"!",
        "日本語の文字起こしを正確に入力します。",
        "한국어 음성을 정확하게 입력합니다.",
        "café déjà vu — naïve façade",
        "Emoji and surrogate pairs: 🙂🚀👩‍💻",
        new string('L', 2_048),
    ];

    [Fact]
    public void NativeInputLayoutMatchesWindowsX64Abi()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(40, Win32TextInjectionService.NativeInputSize);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task DedicatedNativeEditControlReceivesExactCorpus()
    {
        foreach (var expected in ExactCorpus)
        {
            await using var host = await NativeEditControlHost.StartAsync();
            using var service = new Win32TextInjectionService(
                NullLogger<Win32TextInjectionService>.Instance);

            var result = await service.InjectAsync(Request(expected, 1));

            Assert.Equal(TextInjectionStatus.Success, result.Status);
            Assert.Equal(
                expected,
                await host.WaitForExactTextAsync(expected, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task OneHundredConsecutiveCommitsAreExactWithNoSkipPath()
    {
        await using var host = await NativeEditControlHost.StartAsync();
        using var service = new Win32TextInjectionService(
            NullLogger<Win32TextInjectionService>.Instance);
        const string sentence = "Exact repeated sentence 0123456789. ";
        var expected = string.Concat(Enumerable.Repeat(sentence, 100));

        for (var index = 1; index <= 100; index++)
        {
            var result = await service.InjectAsync(Request(sentence, index));
            Assert.Equal(TextInjectionStatus.Success, result.Status);
        }

        Assert.Equal(
            expected,
            await host.WaitForExactTextAsync(expected, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task ConsecutiveCommitBoundariesDoNotAlterText()
    {
        await using var host = await NativeEditControlHost.StartAsync();
        using var service = new Win32TextInjectionService(
            NullLogger<Win32TextInjectionService>.Instance);
        var segments = new[] { "RSTT", " exact", " consecutive", " commits", " 🙂" };
        var expected = string.Concat(segments);

        for (var index = 0; index < segments.Length; index++)
        {
            var result = await service.InjectAsync(Request(segments[index], index + 1));
            Assert.Equal(TextInjectionStatus.Success, result.Status);
        }

        Assert.Equal(
            expected,
            await host.WaitForExactTextAsync(expected, TimeSpan.FromSeconds(5)));
    }

    private static InjectionRequest Request(string text, long commitId) =>
        new(
            new SessionGenerationId(1),
            commitId,
            commitId,
            text,
            DateTimeOffset.UtcNow);

    private sealed class NativeEditControlHost : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _outputPath;
        private readonly Process _process;

        private NativeEditControlHost(string directory, string outputPath, Process process)
        {
            _directory = directory;
            _outputPath = outputPath;
            _process = process;
        }

        public static async Task<NativeEditControlHost> StartAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"rstt-edit-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var readyPath = Path.Combine(directory, "ready.txt");
            var outputPath = Path.Combine(directory, "output.txt");
            var executable = LocateTestHostExecutable();
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(readyPath);
            startInfo.ArgumentList.Add(outputPath);
            var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("The native edit-control host did not start.");
            var host = new NativeEditControlHost(directory, outputPath, process);
            try
            {
                var handles = await WaitForReadyAsync(
                    readyPath,
                    process,
                    TimeSpan.FromSeconds(5));
                Assert.True(
                    await FocusAsync(
                        handles.Window,
                        handles.Editor,
                        process.Id,
                        TimeSpan.FromSeconds(5)),
                    "The dedicated native edit-control host could not become the foreground window.");
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async Task<string> WaitForExactTextAsync(string expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var actual = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_outputPath))
                {
                    try
                    {
                        actual = await File.ReadAllTextAsync(_outputPath);
                        if (string.Equals(actual, expected, StringComparison.Ordinal))
                        {
                            return actual;
                        }
                    }
                    catch (IOException)
                    {
                        // The host rewrites the snapshot on its UI thread.
                    }
                }

                await Task.Delay(20);
            }

            return actual;
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }

            _process.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private static string LocateTestHostExecutable()
        {
            var configuration =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "RSTT.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("The RSTT solution root could not be located.");
            }

            var executable = Path.Combine(
                directory.FullName,
                "tests",
                "RSTT.Input.TestHost",
                "bin",
                configuration,
                "net8.0-windows",
                "RSTT.Input.TestHost.exe");
            return File.Exists(executable)
                ? executable
                : throw new FileNotFoundException(
                    "Build the dedicated input test host before running integration tests.",
                    executable);
        }

        private static async Task<(nint Window, nint Editor)> WaitForReadyAsync(
            string readyPath,
            Process process,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                if (File.Exists(readyPath))
                {
                    try
                    {
                        var text = await File.ReadAllTextAsync(readyPath);
                        var values = text.Split(';');
                        if (values.Length == 2 &&
                            long.TryParse(
                                values[0],
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out var window) &&
                            long.TryParse(
                                values[1],
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out var editor))
                        {
                            return ((nint)window, (nint)editor);
                        }
                    }
                    catch (IOException)
                    {
                        // The host may still have the ready file open for writing.
                    }
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The dedicated native edit-control host did not become ready.");
        }

        private static async Task<bool> FocusAsync(
            nint windowHandle,
            nint editorHandle,
            int expectedProcessId,
            TimeSpan timeout)
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
                    _ = ShowWindow(windowHandle, 9);
                    _ = BringWindowToTop(windowHandle);
                    _ = SetForegroundWindow(windowHandle);
                    _ = SetFocus(editorHandle);
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

                _ = GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
                var guiInfo = new GuiThreadInfo
                {
                    Size = Marshal.SizeOf<GuiThreadInfo>(),
                };
                var editorFocused = GetGUIThreadInfo(targetThread, ref guiInfo) &&
                    guiInfo.FocusWindow == editorHandle;
                if (processId == (uint)expectedProcessId && editorFocused)
                {
                    // Let the activation/ALT handshake and the WinForms focus
                    // transition drain before the first measured Unicode record.
                    await Task.Delay(350);
                    return true;
                }

                await Task.Delay(50);
            }

            return false;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint windowHandle);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint windowHandle, int command);

        [DllImport("user32.dll")]
        private static extern nint SetFocus(nint windowHandle);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(
            uint attachThreadId,
            uint attachToThreadId,
            bool attach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint windowHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public nint ActiveWindow;
            public nint FocusWindow;
            public nint CaptureWindow;
            public nint MenuOwnerWindow;
            public nint MoveSizeWindow;
            public nint CaretWindow;
            public RECT CaretRectangle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
