using System.Net.Http.Json;
using System.Text.Json;

namespace FamiStudioMcp;

// HTTP client for the embedded control server inside a running FamiStudio (launched with -mcpserver).
internal static class ControlClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Sends a command to the running app and returns the raw JSON response text.
    // Throws with a helpful message when the app isn't reachable.
    public static async Task<string> SendAsync(string command, object? args = null, CancellationToken ct = default)
    {
        var url = $"{FamiStudioConfig.ControlUrl}/{command}";
        try
        {
            using var resp = args is null
                ? await Http.PostAsync(url, content: null, ct)
                : await Http.PostAsJsonAsync(url, args, ct);

            var body = await resp.Content.ReadAsStringAsync(ct);
            return body;
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException(
                $"Could not reach FamiStudio control server at {FamiStudioConfig.ControlUrl}. " +
                "Is FamiStudio running with the '-mcpserver' flag? " +
                $"(underlying error: {e.Message})");
        }
    }

    // Pretty-prints the JSON response so it reads cleanly in the client.
    public static async Task<string> SendPrettyAsync(string command, object? args = null, CancellationToken ct = default)
    {
        var raw = await SendAsync(command, args, ct);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw;
        }
    }
}
