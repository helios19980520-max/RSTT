using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RSTT.App.Services;

public enum RsttHotkey
{
    ToggleListening = 1,
    ToggleTextInjection = 2,
    ToggleCaptionOverlay = 3,
}

/// <summary>Uses RegisterHotKey, avoiding a global low-level keyboard hook.</summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private readonly Window _window;
    private HwndSource? _source;
    private nint _handle;
    private bool _disposed;

    public GlobalHotkeyManager(Window window)
    {
        _window = window;
        _window.SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler<RsttHotkey>? HotkeyPressed;

    public bool RegisterDefaults()
    {
        return Register(RsttHotkey.ToggleListening, Key.R)
            & Register(RsttHotkey.ToggleTextInjection, Key.T)
            & Register(RsttHotkey.ToggleCaptionOverlay, Key.C);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
        }

        foreach (var hotkey in Enum.GetValues<RsttHotkey>())
        {
            UnregisterHotKey(_handle, (int)hotkey);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        RegisterDefaults();
    }

    private bool Register(RsttHotkey hotkey, Key key)
    {
        return _handle != IntPtr.Zero && RegisterHotKey(_handle, (int)hotkey, ModAlt | ModControl | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private nint WndProc(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && Enum.IsDefined((RsttHotkey)wParam.ToInt32()))
        {
            handled = true;
            HotkeyPressed?.Invoke(this, (RsttHotkey)wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
