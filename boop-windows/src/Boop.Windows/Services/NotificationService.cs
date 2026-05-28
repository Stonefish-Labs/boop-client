using System.Text.Json;
using Boop.Windows.Models;
using Boop.Windows.ViewModels;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Boop.Windows.Services;

public sealed class NotificationService
{
    private readonly BoopCoreClient _core;
    private readonly MainViewModel _viewModel;
    private bool _isRegistered;
    private bool _reportedUnavailable;

    public NotificationService(BoopCoreClient core, MainViewModel viewModel)
    {
        _core = core;
        _viewModel = viewModel;
        _core.EventReceived += OnCoreEventReceived;
    }

    public void Register()
    {
        AppNotificationManager? manager = null;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                ReportUnavailable("Windows reports that app notifications are unsupported for this Boop build/runtime. Run the installed MSIX package or install the matching Windows App SDK runtime packages to enable local toasts.");
                return;
            }

            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            var iconUri = GetAppIconUri();
            if (iconUri is null)
            {
                manager.Register();
            }
            else
            {
                manager.Register("Boop", iconUri);
            }

            if (manager.Setting != AppNotificationSetting.Enabled)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                ReportUnavailable($"Windows app notifications are {manager.Setting} for Boop.");
                return;
            }

            _isRegistered = true;
        }
        catch (Exception error)
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }
            ReportUnavailable($"Local notifications are unavailable ({error.HResult:X8}): {error.Message}");
        }
    }

    public void HandleCurrentActivation()
    {
        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == ExtendedActivationKind.AppNotification
                && activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                HandleActivation(notificationArgs);
            }
        }
        catch (Exception error)
        {
            ReportUnavailable($"Could not read notification activation ({error.HResult:X8}): {error.Message}");
        }
    }

    public void Unregister()
    {
        if (!_isRegistered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Could not unregister local notifications: {error}");
        }
        finally
        {
            _isRegistered = false;
        }
    }

    public void UpdateBadge(int pendingCount)
    {
        // The tray title is the reliable local-first badge. MSIX tile badge wiring can be added with WNS.
        App.MainWindowInstance?.SetPendingCount(pendingCount);
    }

    private void OnCoreEventReceived(object? sender, BoopCoreEvent e)
    {
        if (e.Event != "notification_candidate")
        {
            return;
        }
        if (!e.Payload.TryGetProperty("event", out var eventElement))
        {
            return;
        }
        var boopEvent = eventElement.Deserialize<BoopEvent>(BoopJson.Options);
        if (boopEvent is not null)
        {
            ShowLocalNotification(boopEvent);
        }
    }

    private void ShowLocalNotification(BoopEvent boopEvent)
    {
        if (!_isRegistered)
        {
            ReportUnavailable("Local notifications are not registered. Boop will keep updating the tray badge, but Windows toasts will not appear.");
            return;
        }

        try
        {
            AppNotificationManager.Default.Show(BuildNotification(boopEvent));
        }
        catch (Exception error)
        {
            ReportUnavailable($"Could not show local notification ({error.HResult:X8}): {error.Message}");
        }
    }

    private static AppNotification BuildNotification(BoopEvent boopEvent)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "open")
            .AddArgument("event_id", boopEvent.Id)
            .AddText(boopEvent.Title)
            .AddText(string.IsNullOrWhiteSpace(boopEvent.Summary) ? boopEvent.BodyMarkdown : boopEvent.Summary)
            .SetTag(boopEvent.Id);

        foreach (var action in boopEvent.Actions.Take(4))
        {
            if (action.RequiresText)
            {
                builder.AddTextBox("text", action.TextPlaceholder ?? action.Label, action.Label);
            }

            var button = new AppNotificationButton(action.Label)
                .AddArgument("action", "submit")
                .AddArgument("event_id", boopEvent.Id)
                .AddArgument("action_id", action.Id);
            if (action.RequiresText)
            {
                button.SetInputId("text");
            }
            builder.AddButton(button);
        }
        if (!boopEvent.Actions.Any())
        {
            builder.AddButton(new AppNotificationButton("Dismiss")
                .AddArgument("action", "submit")
                .AddArgument("event_id", boopEvent.Id)
                .AddArgument("action_id", "dismiss"));
        }

        return builder.BuildNotification();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        HandleActivation(args);
    }

    private void HandleActivation(AppNotificationActivatedEventArgs args)
    {
        var input = args.UserInput.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? "");
        var activation = ToastActivation.Parse(args.Argument, input);
        var window = App.MainWindowInstance;
        if (window is null)
        {
            _ = HandleActivationAsync(activation);
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => _ = HandleActivationAsync(activation));
    }

    private async Task HandleActivationAsync(ToastActivation activation)
    {
        if (activation.Action == "open" && activation.EventId is not null)
        {
            App.MainWindowInstance?.ShowFromTray();
            _viewModel.OpenEvent(activation.EventId);
            return;
        }
        if (activation.Action == "submit" && activation.EventId is not null && activation.ActionId is not null)
        {
            var submitted = await _viewModel.SubmitToastActionAsync(activation.EventId, activation.ActionId, activation.Text);
            if (submitted)
            {
                await AppNotificationManager.Default.RemoveByTagAsync(activation.EventId);
            }
        }
    }

    private static Uri? GetAppIconUri()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png");
        return File.Exists(iconPath) ? new Uri(iconPath) : null;
    }

    private void ReportUnavailable(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        if (_reportedUnavailable)
        {
            return;
        }

        _reportedUnavailable = true;
        _viewModel.ReportError(message);
    }
}
