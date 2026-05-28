using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Boop.Windows.Models;

namespace Boop.Windows.Services;

public sealed class BoopCoreClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private long _nextId;

    public event EventHandler<BoopCoreEvent>? EventReceived;
    public event EventHandler<string>? ErrorReceived;

    public async Task StartAsync()
    {
        if (_process is not null)
        {
            return;
        }

        var executable = ResolveCoreExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start boop-client-core.");
        _stdin = _process.StandardInput;
        _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput, _shutdown.Token));
        _ = Task.Run(() => ReadStderrAsync(_process.StandardError, _shutdown.Token));
        await Task.CompletedTask;
    }

    public Task<BoopClientState?> GetStateAsync() => SendAsync<BoopClientState>("state", null);

    public Task<BoopClientState?> EnrollAsync(string baseUrl, string deviceName, string code, string notificationSound)
    {
        return SendAsync<BoopClientState>("enroll", new
        {
            base_url = baseUrl,
            device_name = deviceName,
            code,
            notification_sound = notificationSound,
        });
    }

    public Task<BoopClientState?> RefreshAsync() => SendAsync<BoopClientState>("refresh", null);

    public Task<BoopClientState?> SelectServerAsync(string serverId)
    {
        return SendAsync<BoopClientState>("select_server", new { server_id = serverId });
    }

    public Task<BoopClientState?> SelectChannelAsync(string channelId)
    {
        return SendAsync<BoopClientState>("select_channel", new { channel_id = channelId });
    }

    public Task<BoopClientState?> SelectEventAsync(string eventId)
    {
        return SendAsync<BoopClientState>("select_event", new { event_id = eventId });
    }

    public Task<BoopClientState?> SetNotificationPreferenceAsync(string notificationSound)
    {
        return SendAsync<BoopClientState>("set_notification_preference", new { notification_sound = notificationSound });
    }

    public async Task<BoopEvent?> SubmitActionAsync(string eventId, string actionId, string? text = null)
    {
        var response = await SendAsync<SubmitActionResponse>("submit_action", new
        {
            event_id = eventId,
            action_id = actionId,
            text,
        });
        return response?.Event;
    }

    public Task ClearChannelAsync(string channelId)
    {
        return SendAsync<JsonElement>("clear_channel", new { channel_id = channelId });
    }

    public Task SetSubscriptionAsync(string channelId, bool subscribed)
    {
        return SendAsync<JsonElement>("set_subscription", new { channel_id = channelId, subscribed });
    }

    public Task ConnectSyncAsync() => SendAsync<JsonElement>("connect_sync", null);

    public Task DisconnectSyncAsync() => SendAsync<JsonElement>("disconnect_sync", null);

    private async Task<T?> SendAsync<T>(string method, object? parameters)
    {
        await StartAsync();
        var stdin = _stdin ?? throw new InvalidOperationException("boop-client-core stdin is unavailable.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var envelope = new
        {
            id,
            method,
            @params = parameters,
        };
        var raw = JsonSerializer.Serialize(envelope, BoopJson.Options);
        await stdin.WriteLineAsync(raw);
        await stdin.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"boop-client-core timed out handling {method}."));
            }
        });

        var result = await completion.Task;
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }
        return result.Deserialize<T>(BoopJson.Options);
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var eventName))
                {
                    EventReceived?.Invoke(this, new BoopCoreEvent
                    {
                        Event = eventName.GetString() ?? "",
                        Payload = root.GetProperty("payload").Clone(),
                    });
                    continue;
                }

                var id = root.GetProperty("id").GetInt64();
                if (!_pending.TryRemove(id, out var completion))
                {
                    continue;
                }
                if (root.GetProperty("ok").GetBoolean())
                {
                    completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(root.GetProperty("error").GetString()));
                }
            }
            catch (Exception error)
            {
                ErrorReceived?.Invoke(this, error.Message);
            }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                ErrorReceived?.Invoke(this, line);
            }
        }
    }

    private static string ResolveCoreExecutable()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("BOOP_CLIENT_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var localName = OperatingSystem.IsWindows() ? "boop-client-core.exe" : "boop-client-core";
        var local = Path.Combine(AppContext.BaseDirectory, localName);
        if (File.Exists(local))
        {
            return local;
        }

        throw new FileNotFoundException("Could not find boop-client-core. Set BOOP_CLIENT_CORE_PATH or copy the sidecar next to Boop.Windows.exe.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                await DisconnectSyncAsync();
            }
        }
        catch
        {
            // Process shutdown is best effort.
        }
        _shutdown.Cancel();
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process shutdown is best effort.
        }
        _process?.Dispose();
        _shutdown.Dispose();
    }

    private sealed class SubmitActionResponse
    {
        public BoopEvent? Event { get; set; }
    }
}
