using System.ComponentModel;
using System.Drawing;
using System.Windows;
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
        _trayIcon = CreateTrayIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        SyncOverlay();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Start / Stop listening", null, (_, _) => _viewModel.StartStopCommand.Execute(null));
        menu.Items.Add("Toggle text injection", null, (_, _) => _viewModel.ToggleTextInjection());
        menu.Items.Add("Toggle captions", null, (_, _) => _viewModel.ToggleCaptionOverlay());
        menu.Items.Add("Open RSTT", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "RSTT — Real-Time Speech-to-Text",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.DoubleClick += (_, _) => ShowMainWindow();
        return icon;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.IsCaptionOverlayEnabled))
        {
            SyncOverlay();
        }
    }

    private void SyncOverlay()
    {
        if (_viewModel.IsCaptionOverlayEnabled)
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _hotkeys.Dispose();
        _trayIcon.Dispose();
        GC.SuppressFinalize(this);
    }
}
