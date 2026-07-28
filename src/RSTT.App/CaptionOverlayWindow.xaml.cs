using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RSTT.App.ViewModels;

namespace RSTT.App;

public partial class CaptionOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x0800_0000;
    private const nint WsExToolWindow = 0x0000_0080;
    private MainViewModel? _viewModel;
    private bool _autoFollow = true;
    private bool _programmaticScroll;
    private bool _trackBounds;

    public CaptionOverlayWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LocationChanged += OnBoundsChanged;
        SizeChanged += OnBoundsChanged;
    }

    public void BeginBoundsTracking()
    {
        _trackBounds = true;
        ApplyInteractionMode();
        FollowLatest();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(
            handle,
            GwlExStyle,
            extendedStyle | WsExNoActivate | WsExToolWindow);
    }

    protected override void OnClosed(EventArgs e)
    {
        AttachViewModel(null);
        DataContextChanged -= OnDataContextChanged;
        LocationChanged -= OnBoundsChanged;
        SizeChanged -= OnBoundsChanged;
        base.OnClosed(e);
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs) =>
        AttachViewModel(eventArgs.NewValue as MainViewModel);

    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.CaptionSegments.CollectionChanged -= OnCaptionSegmentsChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.CaptionSegments.CollectionChanged += OnCaptionSegmentsChanged;
        }

        ApplyInteractionMode();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MainViewModel.CurrentCaptionText) or
            nameof(MainViewModel.IsOverlayPreviewing))
        {
            OnNewCaption();
        }

        if (eventArgs.PropertyName == nameof(MainViewModel.CaptionPositionLocked))
        {
            ApplyInteractionMode();
        }
    }

    private void OnCaptionSegmentsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs) =>
        OnNewCaption();

    private void OnNewCaption()
    {
        if (_autoFollow)
        {
            FollowLatest();
        }
        else
        {
            JumpToLatestButton.Visibility = Visibility.Visible;
        }
    }

    private void FollowLatest()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                _programmaticScroll = true;
                CaptionScrollViewer.ScrollToEnd();
                JumpToLatestButton.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(
                    () => _programmaticScroll = false,
                    System.Windows.Threading.DispatcherPriority.Loaded);
            },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CaptionScrollViewer_ScrollChanged(
        object sender,
        ScrollChangedEventArgs eventArgs)
    {
        if (_programmaticScroll)
        {
            return;
        }

        var distanceFromEnd =
            CaptionScrollViewer.ScrollableHeight -
            CaptionScrollViewer.VerticalOffset;
        if (eventArgs.VerticalChange < 0 && distanceFromEnd > 12)
        {
            _autoFollow = false;
            JumpToLatestButton.Visibility = Visibility.Visible;
            return;
        }

        if (distanceFromEnd <= 4)
        {
            _autoFollow = true;
            JumpToLatestButton.Visibility = Visibility.Collapsed;
        }
    }

    private void JumpToLatest_Click(object sender, RoutedEventArgs eventArgs)
    {
        _autoFollow = true;
        FollowLatest();
    }

    private void Window_MouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs eventArgs) =>
        Toolbar.Opacity = 1;

    private void Window_MouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs eventArgs) =>
        Toolbar.Opacity = 0.18;

    private void Toolbar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (_viewModel?.CaptionPositionLocked != false ||
            FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(
                eventArgs.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (eventArgs.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LockButton_Click(object sender, RoutedEventArgs eventArgs) =>
        ApplyInteractionMode();

    private void DecreaseFont_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.CaptionFontSize -= 2;
        }
    }

    private void IncreaseFont_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.CaptionFontSize += 2;
        }
    }

    private void CloseCaptions_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsCaptionOverlayEnabled = false;
        }
    }

    private void ApplyInteractionMode()
    {
        var unlocked = _viewModel?.CaptionPositionLocked == false;
        ResizeMode = unlocked ? ResizeMode.CanResize : ResizeMode.NoResize;
    }

    private void OnBoundsChanged(object? sender, EventArgs eventArgs)
    {
        if (!_trackBounds ||
            _viewModel is null ||
            WindowState != WindowState.Normal ||
            double.IsNaN(Left) ||
            double.IsNaN(Top))
        {
            return;
        }

        _viewModel.UpdateCaptionBounds(Left, Top, ActualWidth, ActualHeight);
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint value);
}
