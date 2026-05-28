using System.Collections.ObjectModel;
using System.Text.Json;
using Boop.Windows.Models;
using Boop.Windows.Services;
using Microsoft.UI.Dispatching;

namespace Boop.Windows.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly BoopCoreClient _core;
    private readonly DispatcherQueue _dispatcherQueue;
    private BoopClientState _state = new();
    private BoopServerProfile? _selectedServer;
    private BoopChannel? _selectedChannel;
    private BoopEvent? _selectedEvent;
    private string? _errorMessage;
    private bool _isRegistered;
    private bool _isSyncConnected;
    private readonly HashSet<string> _viewDismissSubmittedEventIds = [];
    private string _serverUrl = "";
    private string _deviceName = Environment.MachineName;
    private string _enrollmentCode = "";
    private string _notificationSound = "default";

    public MainViewModel(BoopCoreClient core, DispatcherQueue dispatcherQueue)
    {
        _core = core;
        _dispatcherQueue = dispatcherQueue;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        RegisterCommand = new AsyncCommand(RegisterAsync, () => CanRegister);
        ClearChannelCommand = new AsyncCommand(ClearSelectedChannelAsync, () => SelectedChannel is not null);
        _core.EventReceived += OnCoreEventReceived;
        _core.ErrorReceived += (_, message) => ErrorMessage = message;
    }

    public ObservableCollection<BoopServerProfile> Servers { get; } = [];
    public ObservableCollection<BoopChannel> Channels { get; } = [];
    public ObservableCollection<BoopEvent> Events { get; } = [];
    public ObservableCollection<BoopUserDevice> Devices { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand RegisterCommand { get; }
    public AsyncCommand ClearChannelCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            if (SetProperty(ref _serverUrl, value))
            {
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (SetProperty(ref _deviceName, value))
            {
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EnrollmentCode
    {
        get => _enrollmentCode;
        set
        {
            if (SetProperty(ref _enrollmentCode, value))
            {
                RegisterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NotificationSound
    {
        get => _notificationSound;
        set => SetProperty(ref _notificationSound, value);
    }

    public bool CanRegister =>
        !string.IsNullOrWhiteSpace(ServerUrl)
        && !string.IsNullOrWhiteSpace(DeviceName)
        && EnrollmentCode.Trim().Length >= 8;

    public bool IsRegistered
    {
        get => _isRegistered;
        private set => SetProperty(ref _isRegistered, value);
    }

    public bool IsSyncConnected
    {
        get => _isSyncConnected;
        private set => SetProperty(ref _isSyncConnected, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public int TotalPendingCount => _state.PendingCountsByChannel.Values.Sum();

    public BoopServerProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value) && value is not null)
            {
                _ = _core.SelectServerAsync(value.Id);
            }
        }
    }

    public BoopChannel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value) && value is not null)
            {
                _ = _core.SelectChannelAsync(value.Id);
            }
        }
    }

    public BoopEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value) && value is not null)
            {
                _ = SelectEventAsync(value);
            }
        }
    }

    public async Task InitializeAsync()
    {
        await _core.StartAsync();
        var state = await _core.GetStateAsync();
        if (state is not null)
        {
            ApplyState(state);
        }
        if (IsRegistered)
        {
            await RefreshAsync();
            await _core.ConnectSyncAsync();
        }
    }

    public async Task RegisterAsync()
    {
        ErrorMessage = null;
        var state = await _core.EnrollAsync(ServerUrl, DeviceName, EnrollmentCode, NotificationSound);
        if (state is not null)
        {
            EnrollmentCode = "";
            ApplyState(state);
            await _core.ConnectSyncAsync();
        }
    }

    public async Task RefreshAsync()
    {
        ErrorMessage = null;
        var state = await _core.RefreshAsync();
        if (state is not null)
        {
            ApplyState(state);
        }
    }

    public Task<bool> SubmitActionAsync(BoopEvent boopEvent, BoopAction action, string? text = null)
    {
        return SubmitActionByIdAsync(boopEvent.Id, action.Id, text);
    }

    public Task<bool> SubmitToastActionAsync(string eventId, string actionId, string? text = null)
    {
        return SubmitActionByIdAsync(eventId, actionId, text);
    }

    public async Task ClearSelectedChannelAsync()
    {
        if (SelectedChannel is null)
        {
            return;
        }
        await _core.ClearChannelAsync(SelectedChannel.Id);
    }

    public ValueTask ShutdownAsync() => _core.DisposeAsync();

    public void OpenEvent(string eventId)
    {
        var match = Events.FirstOrDefault(item => item.Id == eventId);
        if (match is not null)
        {
            SelectedEvent = match;
        }
    }

    public void ReportError(string message)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            ErrorMessage = message;
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ErrorMessage = message);
    }

    private async Task SelectEventAsync(BoopEvent boopEvent)
    {
        try
        {
            var state = await _core.SelectEventAsync(boopEvent.Id);
            if (state is not null)
            {
                ApplyState(state);
            }

            var selected = Events.FirstOrDefault(item => item.Id == boopEvent.Id) ?? boopEvent;
            await SubmitViewDismissIfNeededAsync(selected);
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
        }
    }

    private async Task SubmitViewDismissIfNeededAsync(BoopEvent boopEvent)
    {
        var actionId = ViewDismissActionId(boopEvent);
        if (actionId is null || !_viewDismissSubmittedEventIds.Add(boopEvent.Id))
        {
            return;
        }

        try
        {
            var updated = await _core.SubmitActionAsync(boopEvent.Id, actionId);
            if (updated is not null)
            {
                ApplyEventUpdate(updated);
            }
        }
        catch (Exception error)
        {
            _viewDismissSubmittedEventIds.Remove(boopEvent.Id);
            ErrorMessage = error.Message;
        }
    }

    private static string? ViewDismissActionId(BoopEvent boopEvent)
    {
        if (!string.Equals(boopEvent.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (boopEvent.Actions.Count == 0)
        {
            return "dismiss";
        }

        if (boopEvent.Actions.Count != 1)
        {
            return null;
        }

        var action = boopEvent.Actions[0];
        if (action.RequiresText)
        {
            return null;
        }

        return string.Equals(action.Kind, "dismiss", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action.Id, "dismiss", StringComparison.OrdinalIgnoreCase)
            ? action.Id
            : null;
    }

    private void OnCoreEventReceived(object? sender, BoopCoreEvent e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                switch (e.Event)
                {
                    case "state":
                        var state = e.Payload.Deserialize<BoopClientState>(BoopJson.Options);
                        if (state is not null)
                        {
                            ApplyState(state);
                        }
                        break;
                    case "transient_error":
                        if (e.Payload.TryGetProperty("message", out var message))
                        {
                            ErrorMessage = message.GetString();
                        }
                        break;
                    case "resync_required":
                        _ = RefreshAsync();
                        break;
                    case "auth_revoked":
                        ErrorMessage = "This device registration was revoked.";
                        _ = InitializeAsync();
                        break;
                }
            }
            catch (Exception error)
            {
                ErrorMessage = error.Message;
            }
        });
    }

    private void ApplyState(BoopClientState state)
    {
        _state = state;
        IsRegistered = state.IsRegistered;
        IsSyncConnected = state.IsSyncConnected;
        ErrorMessage = state.LastError;
        _notificationSound = state.NotificationSound;

        Replace(Servers, state.Servers);
        foreach (var channel in state.Channels)
        {
            channel.PendingCount = state.PendingCountsByChannel.TryGetValue(channel.Id, out var count) ? count : 0;
        }
        Replace(Channels, state.Channels.Where(channel => channel.Subscribed));
        Replace(Events, state.Events);
        Replace(Devices, state.Devices);

        _selectedServer = Servers.FirstOrDefault(server => server.Id == state.SelectedServerId) ?? Servers.FirstOrDefault();
        _selectedChannel = Channels.FirstOrDefault(channel => channel.Id == state.SelectedChannelId) ?? Channels.FirstOrDefault();
        _selectedEvent = Events.FirstOrDefault(item => item.Id == state.SelectedEventId) ?? Events.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedServer));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(SelectedEvent));
        OnPropertyChanged(nameof(TotalPendingCount));
        OnPropertyChanged(nameof(NotificationSound));
        RefreshCommand.RaiseCanExecuteChanged();
        RegisterCommand.RaiseCanExecuteChanged();
        ClearChannelCommand.RaiseCanExecuteChanged();

        if (_selectedEvent is not null)
        {
            _ = SubmitViewDismissIfNeededAsync(_selectedEvent);
        }
    }

    private async Task<bool> SubmitActionByIdAsync(string eventId, string actionId, string? text)
    {
        ErrorMessage = null;
        try
        {
            var updated = await _core.SubmitActionAsync(eventId, actionId, text);
            if (updated is not null)
            {
                ApplyEventUpdate(updated);
            }
            else
            {
                await RefreshAsync();
            }
            return true;
        }
        catch (Exception error)
        {
            ErrorMessage = error.Message;
            return false;
        }
    }

    private void ApplyEventUpdate(BoopEvent updated)
    {
        var existing = _state.Events.FirstOrDefault(item => item.Id == updated.Id)
            ?? Events.FirstOrDefault(item => item.Id == updated.Id);
        UpdatePendingCount(existing, updated);
        ReplaceEvent(_state.Events, updated);
        ReplaceEvent(Events, updated);
        if (_selectedEvent?.Id == updated.Id)
        {
            _selectedEvent = updated;
            OnPropertyChanged(nameof(SelectedEvent));
        }

        foreach (var channel in Channels)
        {
            channel.PendingCount = _state.PendingCountsByChannel.TryGetValue(channel.Id, out var count) ? count : 0;
        }
        OnPropertyChanged(nameof(TotalPendingCount));
        ClearChannelCommand.RaiseCanExecuteChanged();
    }

    private void UpdatePendingCount(BoopEvent? previous, BoopEvent updated)
    {
        var isPending = string.Equals(updated.Status, "pending", StringComparison.OrdinalIgnoreCase);
        var wasPending = string.Equals(previous?.Status, "pending", StringComparison.OrdinalIgnoreCase)
            || (previous is null && !isPending && _state.PendingCountsByChannel.ContainsKey(updated.ChannelId));
        var previousChannelId = previous?.ChannelId ?? updated.ChannelId;

        if (wasPending && (!isPending || previousChannelId != updated.ChannelId))
        {
            DecrementPendingCount(previousChannelId);
        }
        if (isPending && (!wasPending || previousChannelId != updated.ChannelId))
        {
            _state.PendingCountsByChannel[updated.ChannelId] =
                _state.PendingCountsByChannel.GetValueOrDefault(updated.ChannelId) + 1;
        }
    }

    private void DecrementPendingCount(string channelId)
    {
        if (!_state.PendingCountsByChannel.TryGetValue(channelId, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _state.PendingCountsByChannel.Remove(channelId);
            return;
        }

        _state.PendingCountsByChannel[channelId] = count - 1;
    }

    private static void ReplaceEvent(IList<BoopEvent> events, BoopEvent updated)
    {
        var index = events.ToList().FindIndex(item => item.Id == updated.Id);
        if (index >= 0)
        {
            events[index] = updated;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
