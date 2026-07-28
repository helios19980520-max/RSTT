using System.Globalization;
using System.Diagnostics;

namespace RSTT.Input.TestHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Environment.ExitCode = 2;
            return;
        }

        var readyPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        var eventsPath = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
        ApplicationConfiguration.Initialize();
        using var form = new TestHostForm(readyPath, outputPath, eventsPath);
        Application.Run(form);
    }
}

internal sealed class TestHostForm : Form
{
    private readonly string _readyPath;
    private readonly string _outputPath;
    private readonly string? _eventsPath;
    private readonly RecordingTextBox _editor;
    private readonly System.Windows.Forms.Timer _snapshotTimer;
    private string _lastSnapshot = string.Empty;
    private int _lastEventCount;

    public TestHostForm(string readyPath, string outputPath, string? eventsPath)
    {
        _readyPath = readyPath;
        _outputPath = outputPath;
        _eventsPath = eventsPath;
        Text = "RSTT Native Edit-Control Test Host";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 720;
        Height = 320;
        TopMost = true;
        _editor = new RecordingTextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 12F),
        };
        Controls.Add(_editor);
        _snapshotTimer = new System.Windows.Forms.Timer
        {
            Interval = 25,
            Enabled = true,
        };
        _snapshotTimer.Tick += OnSnapshotTimerTick;
        Shown += OnShown;
        Activated += OnActivated;
    }

    private void OnShown(object? sender, EventArgs eventArgs)
    {
        ActiveControl = _editor;
        Activate();
        _editor.Select();
        _editor.Focus();
        File.WriteAllText(
            _readyPath,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Handle.ToInt64()};{_editor.Handle.ToInt64()}"));
    }

    private void OnActivated(object? sender, EventArgs eventArgs)
    {
        ActiveControl = _editor;
        _editor.Select();
        _editor.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _snapshotTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSnapshotTimerTick(object? sender, EventArgs eventArgs)
    {
        if (string.Equals(_lastSnapshot, _editor.Text, StringComparison.Ordinal))
        {
            return;
        }

        _lastSnapshot = _editor.Text;
        File.WriteAllText(_outputPath, _lastSnapshot);
        if (_eventsPath is not null && _lastEventCount != _editor.InputEvents.Count)
        {
            _lastEventCount = _editor.InputEvents.Count;
            File.WriteAllLines(_eventsPath, _editor.InputEvents);
        }
    }
}

internal sealed class RecordingTextBox : TextBox
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;

    public List<string> InputEvents { get; } = [];

    protected override void WndProc(ref Message message)
    {
        if (message.Msg is WmKeyDown or WmKeyUp or WmChar)
        {
            var repeatCount = unchecked((ushort)(long)message.LParam);
            InputEvents.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Stopwatch.GetTimestamp()};{message.Msg};{message.WParam.ToInt64()};{repeatCount};{GetMessageExtraInfo().ToInt64()}"));
        }

        base.WndProc(ref message);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();
}
