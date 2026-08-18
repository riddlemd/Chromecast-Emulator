using System.Text.Json.Nodes;

namespace ChromecastEmulator.Device;

public sealed class AppSession : IDisposable
{
    private int _nextMediaSessionId;
    private readonly Lock _gate = new();

    public required string AppId { get; init; }
    public required string DisplayName { get; init; }
    public required string SessionId { get; init; }
    public required string TransportId { get; init; }
    public required IReadOnlyList<string> Namespaces { get; init; }
    public string StatusText { get; set; } = "Ready To Cast";
    public bool IsIdleScreen { get; init; }
    public MediaSession? Media { get; private set; }

    public MediaSession StartMedia(JsonObject media, double startTime, bool autoplay, JsonNode? customData)
    {
        // Concurrent LOADs would otherwise both dispose-and-assign, leaking the loser's timer.
        lock (_gate)
        {
            Media?.Dispose();
            Media = new MediaSession(Interlocked.Increment(ref _nextMediaSessionId), media, startTime, autoplay, customData);
            return Media;
        }
    }

    public JsonObject ToJson() => new()
    {
        ["appId"] = AppId,
        ["appType"] = "WEB",
        ["displayName"] = DisplayName,
        ["iconUrl"] = "",
        ["isIdleScreen"] = IsIdleScreen,
        ["launchedFromCloud"] = false,
        ["namespaces"] = new JsonArray(Namespaces.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray()),
        ["sessionId"] = SessionId,
        ["statusText"] = StatusText,
        ["transportId"] = TransportId,
        ["universalAppId"] = AppId,
    };

    public void Dispose()
    {
        lock (_gate) Media?.Dispose();
    }
}
