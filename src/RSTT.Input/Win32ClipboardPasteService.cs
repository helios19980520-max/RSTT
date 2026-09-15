using System.Diagnostics;
using System.Runtime.InteropServices;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Input;

/// <summary>Copies one user-requested batch and sends a single clipboard paste chord.</summary>
public sealed class Win32ClipboardPasteService : ITextInjectionService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IClipboardPasteApi _native;

    public Win32ClipboardPasteService() : this(new ClipboardPasteApi()) { }
    internal Win32ClipboardPasteService(IClipboardPasteApi native) => _native = native;
    public void Dispose() => _gate.Dispose();

    public async Task<TextInjectionResult> InjectAsync(InjectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = _native.ForegroundWindow;
        var process = _native.GetProcessId(target);
        TextInjectionResult Result(TextInjectionStatus status, string message, int sent = 0) =>
            new(status, 4, sent, status == TextInjectionStatus.Success ? request.Text.Length : 0,
                target, process, 0, message, SendInputCallCount: sent > 0 ? 1 : 0, DeliveryProfile: "Clipboard paste");

        if (target == 0 || process == 0) return Result(TextInjectionStatus.Unavailable, "No focused application. Text is still pending.");
        if (process == (uint)Environment.ProcessId) return Result(TextInjectionStatus.SelfFocused, "Focus an input in another application, then press the paste shortcut.");
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result(TextInjectionStatus.Unavailable, "A paste is already in progress.");
        try
        {
            // Wait for the physical shortcut to be released. Never release keys
            // on the user's behalf or send a chord while Ctrl/Alt/Win is held.
            var deadline = Stopwatch.StartNew();
            while (_native.AnyKeyDown)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                    return Result(TextInjectionStatus.Unavailable, "Release the shortcut keys and try again. Text is still pending.");
                if (_native.ForegroundWindow != target)
                    return Result(TextInjectionStatus.TargetChanged, "Focus changed before paste. Text is still pending.");
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_native.ForegroundWindow != target)
                return Result(TextInjectionStatus.TargetChanged, "Focus changed before paste. Text is still pending.");
            if (!_native.TrySetClipboardText(request.Text))
                return Result(TextInjectionStatus.Unavailable, "The clipboard is busy. Text is still pending; try again.");
            if (_native.ForegroundWindow != target || _native.AnyKeyDown)
                return Result(TextInjectionStatus.TargetChanged, "Input or focus changed before paste. Text is still pending.");

            var sent = _native.Paste();
            return sent == 4
                ? Result(TextInjectionStatus.Success, "Pending text pasted.", sent)
                : Result(TextInjectionStatus.Failed, "Windows blocked the paste. Text is still pending. Check the target application's permissions.", sent);
        }
        finally { _gate.Release(); }
    }
}

internal interface IClipboardPasteApi
{
    nint ForegroundWindow { get; }
    uint GetProcessId(nint window);
    bool AnyKeyDown { get; }
    bool TrySetClipboardText(string text);
    int Paste();
}

internal sealed class ClipboardPasteApi : IClipboardPasteApi
{
    public nint ForegroundWindow => GetForegroundWindow();
    public uint GetProcessId(nint window) => GetWindowThreadProcessId(window, out var process) == 0 ? 0 : process;
    public bool AnyKeyDown
    {
        get
        {
            for (var key = 1; key < 255; key++)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
    }

    public bool TrySetClipboardText(string text)
    {
        // The clipboard owns the movable allocation after SetClipboardData succeeds.
        var bytes = checked((text.Length + 1) * sizeof(char));
        var memory = GlobalAlloc(0x0002, (nuint)bytes);
        if (memory == 0) return false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == 0) return false;
            try { Marshal.Copy((text + '\0').ToCharArray(), 0, pointer, text.Length + 1); }
            finally { GlobalUnlock(memory); }
            // Open with a real owner HWND; NULL ownership makes SetClipboardData fail.
            var owner = CreateWindowEx(0, "STATIC", "RSTT Clipboard", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
            if (owner == 0) return false;
            try
            {
                if (!OpenClipboard(owner)) return false;
                try
                {
                    if (!EmptyClipboard() || SetClipboardData(13, memory) == 0) return false;
                    memory = 0;
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally { DestroyWindow(owner); }
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    public int Paste()
    {
        // Shift+Insert invokes the target's normal clipboard paste command.
        // There are no Unicode character events, irrespective of text length.
        var inputs = new[] { Key(0x10), Key(0x2D, 1), Key(0x2D, 3), Key(0x10, 2) };
        var sent = (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<PasteInput>());
        if (sent is > 0 and < 4)
        {
            // Release only keys this operation may have pressed. Never retry paste.
            var releases = new[] { Key(0x2D, 3), Key(0x10, 2) };
            _ = SendInput(2, releases, Marshal.SizeOf<PasteInput>());
        }
        return sent;
    }

    private static PasteInput Key(ushort key, uint flags = 0) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PasteInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, [In] PasteInput[] inputs, int size);
}
