using System.Text.Json;
using System.Text.Json.Serialization;

namespace Boop.Windows.Models;

public static class BoopJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    static BoopJson()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}

public sealed class BoopClientState
{
    public List<BoopServerProfile> Servers { get; set; } = [];
    public string? SelectedServerId { get; set; }
    public BoopUser? CurrentUser { get; set; }
    public List<BoopUserDevice> Devices { get; set; } = [];
    public List<BoopChannel> Channels { get; set; } = [];
    public Dictionary<string, int> PendingCountsByChannel { get; set; } = [];
    public List<BoopEvent> Events { get; set; } = [];
    public string? SelectedChannelId { get; set; }
    public string? SelectedEventId { get; set; }
    public string NotificationSound { get; set; } = "default";
    public bool IsRegistered { get; set; }
    public bool IsSyncConnected { get; set; }
    public string? LastError { get; set; }
}

public sealed class BoopServerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrlString { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
}

public sealed class BoopUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BoopUserDevice
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "unknown";
    public bool HasApnsToken { get; set; }
    public string NotificationSound { get; set; } = "default";
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class BoopChannel : ObservableObject
{
    private int _pendingCount;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#3B82F6";
    public int DefaultPriority { get; set; }
    public string Privacy { get; set; } = "private";
    public bool Subscribed { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    [JsonIgnore]
    public int PendingCount
    {
        get => _pendingCount;
        set => SetProperty(ref _pendingCount, value);
    }
}

public sealed class BoopField
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Style { get; set; }
}

public sealed class BoopLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class BoopAction
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "custom";
    public string Style { get; set; } = "default";
    public bool RequiresText { get; set; }
    public string? TextPlaceholder { get; set; }
    public bool Destructive { get; set; }
    public bool Foreground { get; set; }
}

public sealed class BoopEventResult
{
    public string EventId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string ActionKind { get; set; } = "";
    public string ActionLabel { get; set; } = "";
    public string? Text { get; set; }
    public string? ActorUserId { get; set; }
    public string? ActorDeviceId { get; set; }
    public DateTimeOffset ResolvedAt { get; set; }
}

public sealed class BoopEventUserResult
{
    public string UserId { get; set; } = "";
    public BoopEventResult Result { get; set; } = new();
}

public sealed class BoopEvent : ObservableObject
{
    public string Id { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public List<string> Recipients { get; set; } = [];
    public string ActionResolution { get; set; } = "shared";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string BodyMarkdown { get; set; } = "";
    public List<BoopField> Fields { get; set; } = [];
    public List<BoopLink> Links { get; set; } = [];
    public string? ImageUrl { get; set; }
    public int Priority { get; set; }
    public string Privacy { get; set; } = "private";
    public string? DedupeKey { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public BoopEventResult? Result { get; set; }
    public List<BoopEventUserResult> UserResults { get; set; } = [];
    public string? CallbackUrl { get; set; }
    public List<BoopAction> Actions { get; set; } = [];

    [JsonIgnore]
    public string CreatedRelative => CreatedAt.ToLocalTime().ToString("g");
}

public sealed class BoopCoreEvent
{
    public string Event { get; set; } = "";
    public JsonElement Payload { get; set; }
}

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : System.Windows.Input.ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        try
        {
            _isRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
