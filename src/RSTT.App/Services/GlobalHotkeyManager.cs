using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RSTT.Core.Hotkeys;

namespace RSTT.App.Services;

public enum RsttHotkey
{
    ToggleListening,
    ToggleTextInjection,
    ToggleCaptionOverlay,
}

public sealed class HotkeyRegistrationFailedEventArgs(
    RsttHotkey hotkey,
    string requestedGesture,
    string message) : EventArgs
{
    public RsttHotkey Hotkey { get; } = hotkey;

    public string RequestedGesture { get; } = requestedGesture;

    public string Message { get; } = message;
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<RsttHotkey>? HotkeyPressed;

    event EventHandler<HotkeyRegistrationFailedEventArgs>? RegistrationFailed;

    bool IsInitialized { get; }

    string GetGesture(RsttHotkey hotkey);

    bool TryReplace(RsttHotkey hotkey, string gesture, out string failureMessage);

    bool ResetDefaults();
}

/// <summary>
/// RegisterHotKey-backed global shortcuts. Replacements are transactional: the new
/// registration is acquired under a fresh ID before the working registration is released.
/// </summary>
public sealed partial class GlobalHotkeyManager : IGlobalHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int FirstRegistrationId = 0x5200;
    private readonly Window _window;
    private readonly ILogger<GlobalHotkeyManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<RsttHotkey, RegisteredHotkey> _registrations = [];
    private readonly Dictionary<int, RsttHotkey> _actionsById = [];
    private HwndSource? _source;
    private nint _handle;
    private int _nextRegistrationId = FirstRegistrationId;
    private bool _disposed;

    public GlobalHotkeyManager(
        Window window,
        ILogger<GlobalHotkeyManager> logger)
    {
        _window = window;
        _logger = logger;
        _window.SourceInitialized += OnSourceInitialized;
        // Loaded is an idempotent fallback for windows whose HWND was created while
        // XAML/WindowChrome was being initialized before this service subscribed.
        _window.Loaded += OnWindowLoaded;
    }

    public event EventHandler<RsttHotkey>? HotkeyPressed;

    public event EventHandler<HotkeyRegistrationFailedEventArgs>? RegistrationFailed;

    public bool IsInitialized => _handle != nint.Zero;

    public string GetGesture(RsttHotkey hotkey)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue(hotkey, out var registration)
                ? registration.Gesture.ToString()
                : GetDefaultGesture(hotkey);
        }
    }

    public bool TryReplace(
        RsttHotkey hotkey,
        string gesture,
        out string failureMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HotkeyGestureParser.TryParse(
                gesture,
                out var parsed,
                out failureMessage))
        {
            LogRegistrationRejected(_logger, hotkey, gesture, failureMessage);
            return false;
        }

        lock (_gate)
        {
            if (_handle == nint.Zero)
            {
                failureMessage = "The application window is not ready for global shortcuts.";
                LogRegistrationRejected(_logger, hotkey, gesture, failureMessage);
                return false;
            }

            if (_registrations.TryGetValue(hotkey, out var current) &&
                current.Gesture == parsed)
            {
                failureMessage = string.Empty;
                return true;
            }

            var duplicate = _registrations.FirstOrDefault(pair =>
                pair.Key != hotkey && pair.Value.Gesture == parsed);
            if (!duplicate.Equals(default(KeyValuePair<RsttHotkey, RegisteredHotkey>)))
            {
                failureMessage =
                    $"That shortcut is already assigned to {FormatAction(duplicate.Key)}.";
                LogRegistrationRejected(_logger, hotkey, gesture, failureMessage);
                return false;
            }

            var newId = NextRegistrationId();
            if (!RegisterHotKey(
                    _handle,
                    newId,
                    (uint)parsed.Modifiers,
                    parsed.VirtualKey))
            {
                var nativeError = Marshal.GetLastWin32Error();
                failureMessage = nativeError == 1409
                    ? "This shortcut is already being used by another application."
                    : $"Windows could not register this shortcut: {new Win32Exception(nativeError).Message}";
                LogRegistrationRejected(_logger, hotkey, gesture, failureMessage);
                return false;
            }

            // The requested shortcut is now guaranteed to work. Only then release
            // the previous ID and swap the routing table.
            if (current is not null)
            {
                UnregisterHotKey(_handle, current.Id);
                _actionsById.Remove(current.Id);
            }

            var replacement = new RegisteredHotkey(newId, parsed);
            _registrations[hotkey] = replacement;
            _actionsById[newId] = hotkey;
            failureMessage = string.Empty;
            LogRegistrationSucceeded(_logger, hotkey, parsed.ToString(), newId);
            return true;
        }
    }

    public bool ResetDefaults()
    {
        var succeeded = true;
        foreach (var hotkey in Enum.GetValues<RsttHotkey>())
        {
            var gesture = GetDefaultGesture(hotkey);
            if (TryReplace(hotkey, gesture, out var error))
            {
                continue;
            }

            succeeded = false;
            RegistrationFailed?.Invoke(
                this,
                new HotkeyRegistrationFailedEventArgs(hotkey, gesture, error));
        }

        return succeeded;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnWindowLoaded;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
        }

        lock (_gate)
        {
            foreach (var registration in _registrations.Values)
            {
                UnregisterHotKey(_handle, registration.Id);
            }

            _registrations.Clear();
            _actionsById.Clear();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        InitializeWindowHandle();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        InitializeWindowHandle();
    }

    private void InitializeWindowHandle()
    {
        if (_handle != nint.Zero)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == nint.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        LogWindowInitialized(_logger, _handle, _source is not null);
    }

    private nint WndProc(
        nint handle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return nint.Zero;
        }

        RsttHotkey action;
        lock (_gate)
        {
            if (!_actionsById.TryGetValue(wParam.ToInt32(), out action))
            {
                return nint.Zero;
            }
        }

        handled = true;
        LogHotkeyReceived(_logger, action, wParam.ToInt32());
        HotkeyPressed?.Invoke(this, action);
        return nint.Zero;
    }

    private int NextRegistrationId()
    {
        while (_actionsById.ContainsKey(_nextRegistrationId))
        {
            _nextRegistrationId++;
        }

        if (_nextRegistrationId > 0xBFFF)
        {
            _nextRegistrationId = FirstRegistrationId;
            while (_actionsById.ContainsKey(_nextRegistrationId))
            {
                _nextRegistrationId++;
            }
        }

        return _nextRegistrationId++;
    }

    private static string GetDefaultGesture(RsttHotkey hotkey) =>
        hotkey switch
        {
            RsttHotkey.ToggleListening => "Ctrl+Alt+R",
            RsttHotkey.ToggleTextInjection => "Ctrl+Alt+T",
            RsttHotkey.ToggleCaptionOverlay => "Ctrl+Alt+C",
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey)),
        };

    private static string FormatAction(RsttHotkey hotkey) =>
        hotkey switch
        {
            RsttHotkey.ToggleListening => "Start / stop listening",
            RsttHotkey.ToggleTextInjection => "Toggle text injection",
            RsttHotkey.ToggleCaptionOverlay => "Toggle captions",
            _ => hotkey.ToString(),
        };

    private sealed record RegisteredHotkey(int Id, HotkeyGesture Gesture);

    [LoggerMessage(
        LogLevel.Information,
        "Global hotkey window initialized. HWND={WindowHandle}, hook attached={HookAttached}.")]
    private static partial void LogWindowInitialized(
        ILogger logger,
        nint windowHandle,
        bool hookAttached);

    [LoggerMessage(
        LogLevel.Information,
        "Registered global hotkey {Action} as {Gesture} with ID {RegistrationId}.")]
    private static partial void LogRegistrationSucceeded(
        ILogger logger,
        RsttHotkey action,
        string gesture,
        int registrationId);

    [LoggerMessage(
        LogLevel.Warning,
        "Could not register global hotkey {Action} as {Gesture}: {Message}")]
    private static partial void LogRegistrationRejected(
        ILogger logger,
        RsttHotkey action,
        string gesture,
        string message);

    [LoggerMessage(
        LogLevel.Information,
        "Received global hotkey {Action} with ID {RegistrationId}.")]
    private static partial void LogHotkeyReceived(
        ILogger logger,
        RsttHotkey action,
        int registrationId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}
