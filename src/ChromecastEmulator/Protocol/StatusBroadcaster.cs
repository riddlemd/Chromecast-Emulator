using System.Text.Json.Nodes;
using ChromecastEmulator.Device;

namespace ChromecastEmulator.Protocol;

public sealed class StatusBroadcaster
{
    private readonly IConnectionRegistry _registry;
    private readonly VirtualDevice _device;

    public StatusBroadcaster(IConnectionRegistry registry, VirtualDevice device)
    {
        _registry = registry;
        _device = device;
    }

    /// Direct replies and broadcasts share these builders so the two paths cannot
    /// drift in envelope shape.
    public JsonObject ReceiverStatusEnvelope(int requestId) => new()
    {
        ["requestId"] = requestId,
        ["type"] = "RECEIVER_STATUS",
        ["status"] = _device.BuildReceiverStatus(),
    };

    public JsonObject MediaStatusEnvelope(AppSession session, int requestId, bool includeMedia)
    {
        var statuses = new JsonArray();
        if (session.Media is { } media) statuses.Add(media.BuildStatus(_device.Volume, includeMedia));

        return new JsonObject
        {
            ["requestId"] = requestId,
            ["type"] = "MEDIA_STATUS",
            ["status"] = statuses,
        };
    }

    public Task ReceiverStatusAsync(CancellationToken ct) =>
        FanOutAsync(CastNamespaces.PlatformReceiverId, CastNamespaces.Receiver, ReceiverStatusEnvelope(0), ct);

    public Task MediaStatusAsync(AppSession session, bool includeMedia, CancellationToken ct) =>
        FanOutAsync(session.TransportId, CastNamespaces.Media, MediaStatusEnvelope(session, 0, includeMedia), ct);

    public async Task FanOutAsync(string source, string ns, JsonNode payload, CancellationToken ct)
    {
        // Serialize once, send concurrently: a stalled sender must not head-of-line
        // block every other sender's status update.
        var payloadUtf8 = payload.ToJsonString();
        var sends = new List<Task>();

        foreach (var connection in _registry.Connections)
        {
            foreach (var sender in connection.SendersFor(source))
                sends.Add(connection.SendUtf8Async(source, sender, ns, payloadUtf8, ct));
        }

        await Task.WhenAll(sends);
    }
}
