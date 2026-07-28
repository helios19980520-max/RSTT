using System.Globalization;

namespace RSTT.Input.TestHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Environment.ExitCode = 2;
            return;
        }

        var readyPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        ApplicationConfiguration.Initialize();
        using var form = new TestHostForm(readyPath, outputPath);
        Application.Run(form);
    }
}

internal sealed class TestHostForm : Form
{
    private readonly string _readyPath;
    private readonly string _outputPath;
    private readonly TextBox _editor;
    private readonly System.Windows.Forms.Timer _snapshotTimer;
    private string _lastSnapshot = string.Empty;

    public TestHostForm(string readyPath, string outputPath)
    {
        _readyPath = readyPath;
        _outputPath = outputPath;
        Text = "RSTT Native Edit-Control Test Host";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 720;
        Height = 320;
        TopMost = true;
        _editor = new TextBox
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
    }
}
