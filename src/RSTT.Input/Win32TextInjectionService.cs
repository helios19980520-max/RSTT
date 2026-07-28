using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;

namespace RSTT.Input;

/// <summary>Serializes Unicode SendInput calls and refuses to type into RSTT itself.</summary>
public sealed partial class Win32TextInjectionService : ITextInjectionService, IDisposable
{
    private const int BatchCharacterCount = 16;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFUnicode = 0x0004;
    private const uint KeyEventFKeyUp = 0x0002;
    private readonly ILogger<Win32TextInjectionService> _logger;
    private readonly SemaphoreSlim _injectionLock = new(1, 1);
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private bool _disposed;

    public Win32TextInjectionService(ILogger<Win32TextInjectionService> logger)
    {
        _logger = logger;
    }

    internal static int NativeInputSize => Marshal.SizeOf<INPUT>();

    public async Task<TextInjectionResult> InjectTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextInjectionResult(true);
        }

        await _injectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = GetForegroundWindow();
            if (target == IntPtr.Zero)
            {
                return new TextInjectionResult(
                    false,
                    "No foreground window is available for text injection.",
                    TextInjectionStatus.NoTarget);
            }

            if (GetWindowThreadProcessId(target, out var targetProcessId) == 0)
            {
                return new TextInjectionResult(
                    false,
                    "The foreground window could not be identified.",
                    TextInjectionStatus.NoTarget);
            }
            if (targetProcessId == _currentProcessId)
            {
                return new TextInjectionResult(
                    false,
                    "RSTT is focused, so text was not injected into its own window.",
                    TextInjectionStatus.SelfFocused);
            }

            var expectedInputCount = text.Length * 2;
            var sentInputCount = 0u;
            for (var offset = 0; offset < text.Length; offset += BatchCharacterCount)
            {
                var currentTarget = GetForegroundWindow();
                if (currentTarget == IntPtr.Zero ||
                    GetWindowThreadProcessId(currentTarget, out var currentTargetProcessId) == 0 ||
                    currentTargetProcessId != targetProcessId)
                {
                    const string message = "Text injection stopped because the foreground application changed.";
                    LogPartialSend(_logger, sentInputCount, expectedInputCount, targetProcessId, message);
                    return new TextInjectionResult(false, message, TextInjectionStatus.TargetChanged);
                }

                var characterCount = Math.Min(BatchCharacterCount, text.Length - offset);
                var inputs = CreateUnicodeInputs(text.AsSpan(offset, characterCount));
                var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
                sentInputCount += sent;
                if (sent != (uint)inputs.Length)
                {
                    var error = Marshal.GetLastWin32Error();
                    var message = error == 5
                        ? "Text injection is unavailable for the elevated foreground application."
                        : new Win32Exception(error).Message;
                    LogPartialSend(_logger, sentInputCount, expectedInputCount, targetProcessId, message);
                    return new TextInjectionResult(
                        false,
                        message,
                        error == 5 ? TextInjectionStatus.ElevatedTarget : TextInjectionStatus.Failed);
                }

                // Keep target checks between batches without turning every UTF-16 code unit
                // into a separate P/Invoke and scheduler delay.
                if (offset + characterCount < text.Length)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
            }

            LogInjectedText(_logger, text.Length, targetProcessId);
            return new TextInjectionResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogInjectionFailure(_logger, exception);
            return new TextInjectionResult(
                false,
                "Text injection failed. See local logs for details.",
                TextInjectionStatus.Failed);
        }
        finally
        {
            _injectionLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _injectionLock.Dispose();
    }

    private static INPUT[] CreateUnicodeInputs(ReadOnlySpan<char> characters)
    {
        var inputs = new INPUT[characters.Length * 2];
        for (var index = 0; index < characters.Length; index++)
        {
            inputs[index * 2] = CreateInput(characters[index], KeyEventFUnicode);
            inputs[(index * 2) + 1] = CreateInput(
                characters[index],
                KeyEventFUnicode | KeyEventFKeyUp);
        }

        return inputs;
    }

    private static INPUT CreateInput(char character, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KEYBDINPUT
        {
            VirtualKey = 0,
            ScanCode = character,
            Flags = flags,
        },
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, [In] INPUT[] inputs, int size);

    [LoggerMessage(LogLevel.Warning, "SendInput sent {Sent} of {Expected} input records to process {ProcessId}: {Message}")]
    private static partial void LogPartialSend(ILogger logger, uint sent, int expected, uint processId, string message);

    [LoggerMessage(LogLevel.Debug, "Injected {CharacterCount} Unicode characters into foreground process {ProcessId}.")]
    private static partial void LogInjectedText(ILogger logger, int characterCount, uint processId);

    [LoggerMessage(LogLevel.Error, "Text injection failed.")]
    private static partial void LogInjectionFailure(ILogger logger, Exception exception);

    // RSTT publishes x64-only. INPUT's native anonymous union starts at byte 8
    // and the complete structure is 40 bytes on x64. Expressing that layout
    // directly also avoids array-marshalling ambiguity from a nested union.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(8)]
        public MOUSEINPUT Mouse;

        [FieldOffset(8)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
