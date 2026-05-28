using Boop.Windows.Services;
using Boop.Windows.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Boop.Windows;

public partial class App : Application
{
    private BoopCoreClient? _core;
    private NotificationService? _notifications;

    public static MainWindow? MainWindowInstance { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _core = new BoopCoreClient();
        var viewModel = new MainViewModel(_core, DispatcherQueue.GetForCurrentThread());
        _notifications = new NotificationService(_core, viewModel);
        MainWindowInstance = new MainWindow(viewModel, _notifications);
        _notifications.Register();
        _notifications.HandleCurrentActivation();
        MainWindowInstance.Activate();
        _ = viewModel.InitializeAsync();
    }
}
