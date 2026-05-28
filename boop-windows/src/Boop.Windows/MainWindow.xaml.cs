using System.Runtime.InteropServices;
using Boop.Windows.Services;
using Boop.Windows.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Boop.Windows;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NotificationService _notificationService;
    private readonly TrayIconService _tray;
    private readonly nint _hwnd;
    private bool _isQuitting;

    public MainWindow(MainViewModel viewModel, NotificationService notificationService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notificationService = notificationService;
        if (Content is FrameworkElement root)
        {
            root.DataContext = viewModel;
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = "Boop";
        appWindow.Closing += OnClosing;
        _tray = new TrayIconService(ShowFromTray, Quit);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TotalPendingCount))
            {
                _notificationService.UpdateBadge(viewModel.TotalPendingCount);
            }
        };
    }

    public void SetPendingCount(int count)
    {
        _tray.SetPendingCount(count);
        Title = count > 0 ? $"Boop ({count})" : "Boop";
    }

    public void ShowFromTray()
    {
        ShowWindow(_hwnd, 5);
        Activate();
    }

    private void Quit()
    {
        _isQuitting = true;
        _notificationService.Unregister();
        _tray.Dispose();
        _ = _viewModel.ShutdownAsync();
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isQuitting)
        {
            return;
        }
        args.Cancel = true;
        ShowWindow(_hwnd, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}

public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class SyncStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "Live sync connected" : "Live sync disconnected";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class CountVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool boolValue && boolValue ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
