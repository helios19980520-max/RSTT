using System.Windows;
using RSTT.App.Services;
using RSTT.Core.Hotkeys;

namespace RSTT.App;

public partial class ShortcutRecorderWindow : Window
{
    private readonly GlobalHotkeyManager _hotkeys;
    private readonly RsttHotkey _action;
    private string _candidate = string.Empty;
    private bool _closing;

    public ShortcutRecorderWindow(GlobalHotkeyManager hotkeys, RsttHotkey action)
    {
        InitializeComponent();
        _hotkeys = hotkeys;
        _action = action;
        Loaded += (_, _) =>
        {
            _hotkeys.ShortcutRecorded += OnRecorded;
            if (!_hotkeys.BeginRecording(out var error)) CaptureError.Text = error;
        };
        Closed += (_, _) =>
        {
            _hotkeys.EndRecording();
            _hotkeys.ShortcutRecorded -= OnRecorded;
        };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing && IsVisible) Close(); };
    }

    private void OnRecorded(object? sender, string value)
    {
        if (value == "Escape") { Close(); return; }
        DetectedShortcut.Text = value;
        ConfirmButton.IsEnabled = HotkeyGestureParser.TryParse(value, out var gesture, out var error);
        _candidate = ConfirmButton.IsEnabled ? gesture.ToString() : string.Empty;
        CaptureError.Text = error;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_hotkeys.TryReplace(_action, _candidate, out var error))
        {
            CaptureError.Text = error;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
