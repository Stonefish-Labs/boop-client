namespace Boop.Windows.Services;

public sealed record ToastActivation(string Action, string? EventId, string? ActionId, string? Text)
{
    public static ToastActivation Parse(string arguments, IReadOnlyDictionary<string, string>? userInput = null)
    {
        var parsed = ParseQuery(arguments);
        var text = userInput is not null && userInput.TryGetValue("text", out var value) ? value : null;
        return new ToastActivation(
            parsed.GetValueOrDefault("action") ?? "open",
            parsed.GetValueOrDefault("event_id"),
            parsed.GetValueOrDefault("action_id"),
            text);
    }

    private static Dictionary<string, string> ParseQuery(string value)
    {
        return value
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')),
                StringComparer.OrdinalIgnoreCase);
    }
}
