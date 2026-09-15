using System.ComponentModel;
using System.Runtime.InteropServices;
using RSTT.Core.Hotkeys;

namespace RSTT.App.Services;

public sealed partial class GlobalHotkeyManager
{
    private delegate nint LowLevelHook(int code, nint message, nint data);
    private LowLevelHook? _keyboardCallback;
    private LowLevelHook? _mouseCallback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private bool _middleDown;
    private bool _middleUsed;
    private bool _swallowMiddleUp;
    private HotkeyModifiers _middleModifiers;
    private readonly HashSet<uint> _suppressedKeys = [];

    public bool IsRecording { get; private set; }
    public event EventHandler<string>? ShortcutRecorded;

    public bool BeginRecording(out string error)
    {
        if (!EnsureInputHooks(out error)) return false;
        IsRecording = true;
        _middleDown = false;
        _middleUsed = false;
        return true;
    }

    public void EndRecording()
    {
        IsRecording = false;
        _middleUsed = true;
    }

    private bool EnsureInputHooks(out string error)
    {
        if (_keyboardHook != 0 && _mouseHook != 0) { error = string.Empty; return true; }
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
        if (_keyboardHook != 0 && _mouseHook != 0) { error = string.Empty; return true; }
        error = $"Windows could not capture input: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        DisposeInputHooks();
        return false;
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardHookData>(data);
        if ((key.Flags & 0x10) != 0) return CallNextHookEx(0, code, message, data);
        var down = message == 0x100 || message == 0x104;
        var up = message == 0x101 || message == 0x105;
        if (up && _suppressedKeys.Remove(key.VirtualKey)) return 1;
        if (down && _suppressedKeys.Contains(key.VirtualKey)) return 1;
        if (IsRecording && down && !IsModifier(key.VirtualKey))
        {
            var gesture = key.VirtualKey == 0x1B ? "Escape" :
                new HotkeyGesture(CurrentModifiers(), key.VirtualKey, _middleDown).ToString();
            _suppressedKeys.Add(key.VirtualKey);
            _window.Dispatcher.BeginInvoke(() => { if (IsRecording) ShortcutRecorded?.Invoke(this, gesture); });
            return 1;
        }

        if (!IsRecording && down && _middleDown && !_middleUsed && !IsModifier(key.VirtualKey))
        {
            var gesture = new HotkeyGesture(CurrentModifiers(), key.VirtualKey, true);
            if (TryDispatchMouseGesture(gesture))
            {
                _middleUsed = true;
                _suppressedKeys.Add(key.VirtualKey);
                return 1;
            }
            _middleUsed = true; // An unmatched chord must not become a plain MMB paste on release.
        }
        return CallNextHookEx(0, code, message, data);
    }

    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || message != 0x207 && message != 0x208)
            return CallNextHookEx(0, code, message, data);
        var mouse = Marshal.PtrToStructure<MouseHookData>(data);
        if ((mouse.Flags & 1) != 0) return CallNextHookEx(0, code, message, data);
        if (message == 0x207)
        {
            _middleDown = true;
            _middleUsed = false;
            _middleModifiers = CurrentModifiers();
            _swallowMiddleUp = IsRecording || _registrations.Values.Any(item =>
                item.Gesture.MiddleMouse && item.Gesture.Modifiers == _middleModifiers);
            if (IsRecording)
            {
                var gesture = new HotkeyGesture(_middleModifiers, 0, true).ToString();
                _window.Dispatcher.BeginInvoke(() => { if (IsRecording) ShortcutRecorded?.Invoke(this, gesture); });
            }
            if (_swallowMiddleUp) return 1;
        }
        else
        {
            _middleDown = false;
            if (!IsRecording && !_middleUsed && _swallowMiddleUp)
                TryDispatchMouseGesture(new HotkeyGesture(_middleModifiers, 0, true));
            if (_swallowMiddleUp) { _swallowMiddleUp = false; return 1; }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private bool TryDispatchMouseGesture(HotkeyGesture gesture)
    {
        foreach (var pair in _registrations)
        {
            if (pair.Value.Gesture != gesture) continue;
            var action = pair.Key;
            _window.Dispatcher.BeginInvoke(() => { if (!IsRecording && !_disposed) HotkeyPressed?.Invoke(this, action); });
            return true;
        }
        return false;
    }

    private static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.NoRepeat;
        if (Down(0x11)) modifiers |= HotkeyModifiers.Control;
        if (Down(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (Down(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (Down(0x5B) || Down(0x5C)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void DisposeInputHooks()
    {
        IsRecording = false;
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = 0;
        _mouseHook = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, LowLevelHook callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
