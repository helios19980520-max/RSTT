using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using RSTT.App.Services;
using RSTT.App.ViewModels;

namespace RSTT.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly CaptionOverlayWindow _overlay;
    private readonly GlobalHotkeyManager _hotkeys;
    private bool _allowClose;
    private bool _disposed;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _overlay = new CaptionOverlayWindow { DataContext = viewModel };
        _overlay.ContentRendered += PositionOverlay;
        _hotkeys = new GlobalHotkeyManager(this);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _hotkeys.RegistrationFailed += OnHotkeyRegistrationFailed;
        _trayIcon = CreateTrayIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        SyncOverlay();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(16, 27, 43),
            ForeColor = Color.FromArgb(242, 247, 250),
            ShowImageMargin = false,
            Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new RsttColorTable()),
            Padding = new System.Windows.Forms.Padding(5),
        };
        menu.Items.Add("Start / Stop listening", null, (_, _) => _viewModel.StartStopCommand.Execute(null));
        menu.Items.Add("Toggle text injection", null, (_, _) => _viewModel.ToggleTextInjection());
        menu.Items.Add("Toggle captions", null, (_, _) => _viewModel.ToggleCaptionOverlay());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open RSTT", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.Padding = new System.Windows.Forms.Padding(9, 5, 9, 5);
            item.Font = new Font("Segoe UI", 9);
        }
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateBrandIcon(),
            Text = "RSTT — Real-Time Speech-to-Text",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => ShowMainWindow();
        return icon;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainViewModel.IsCaptionOverlayEnabled)
            or nameof(MainViewModel.IsListening)
            or nameof(MainViewModel.HasTranscript)
            or nameof(MainViewModel.IsOverlayPreviewing))
        {
            SyncOverlay();
        }

        if (eventArgs.PropertyName is nameof(MainViewModel.CaptionWidth) or nameof(MainViewModel.CaptionFontSize))
        {
            Dispatcher.BeginInvoke(() => PositionOverlay(this, EventArgs.Empty));
        }

        if (eventArgs.PropertyName == nameof(MainViewModel.StatusLabel))
        {
            _trayIcon.Text = $"RSTT — {_viewModel.StatusLabel}";
        }
    }

    private void SyncOverlay()
    {
        if (_viewModel.IsCaptionOverlayEnabled
            && (_viewModel.IsListening || _viewModel.HasTranscript || _viewModel.IsOverlayPreviewing))
        {
            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!_allowClose && _viewModel.MinimizeToTray)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _overlay.ContentRendered -= PositionOverlay;
        _overlay.Close();
        Dispose();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void PositionOverlay(object? sender, EventArgs eventArgs)
    {
        var workArea = SystemParameters.WorkArea;
        _overlay.Left = workArea.Left + Math.Max(16, (workArea.Width - _overlay.ActualWidth) / 2);
        _overlay.Top = workArea.Bottom - _overlay.ActualHeight - 40;
    }

    private void OnHotkeyPressed(object? sender, RsttHotkey hotkey)
    {
        switch (hotkey)
        {
            case RsttHotkey.ToggleListening:
                _viewModel.StartStopCommand.Execute(null);
                break;
            case RsttHotkey.ToggleTextInjection:
                _viewModel.ToggleTextInjection();
                break;
            case RsttHotkey.ToggleCaptionOverlay:
                _viewModel.ToggleCaptionOverlay();
                break;
        }
    }

    private void OnHotkeyRegistrationFailed(object? sender, EventArgs eventArgs) =>
        _viewModel.ReportHotkeyRegistrationFailure();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (eventArgs.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs eventArgs) =>
        ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs) =>
        Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static System.Drawing.Icon CreateBrandIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(8, 13, 22));
            using var accent = new SolidBrush(Color.FromArgb(40, 215, 196));
            graphics.FillEllipse(accent, 2, 2, 28, 28);
            using var textBrush = new SolidBrush(Color.FromArgb(6, 35, 31));
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var textSize = graphics.MeasureString("R", font);
            graphics.DrawString("R", font, textBrush, (32 - textSize.Width) / 2, (32 - textSize.Height) / 2);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _hotkeys.RegistrationFailed -= OnHotkeyRegistrationFailed;
        _hotkeys.Dispose();
        _trayIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint iconHandle);

    private sealed class RsttColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(16, 27, 43);
        public override Color ImageMarginGradientBegin => Color.FromArgb(16, 27, 43);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(16, 27, 43);
        public override Color ImageMarginGradientEnd => Color.FromArgb(16, 27, 43);
        public override Color MenuItemSelected => Color.FromArgb(23, 58, 61);
        public override Color MenuItemBorder => Color.FromArgb(40, 215, 196);
        public override Color SeparatorDark => Color.FromArgb(32, 52, 76);
        public override Color SeparatorLight => Color.FromArgb(32, 52, 76);
    }
}
