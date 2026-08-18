using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;

namespace ChromecastEmulator.Protocol;

/// Senders tear the socket down after roughly 10s without a PONG, so this must
/// answer even when nothing else about the message is understood.
public sealed class HeartbeatHandler : ICastNamespaceHandler
{
    public async Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        string? type;
        try
        {
            type = JsonNode.Parse(message.PayloadUtf8)?["type"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            // Throwing here would reach the router's catch and no PONG would ever go out.
            return;
        }

        if (type == "PING")
            await connection.ReplyAsync(message, new JsonObject { ["type"] = "PONG" }, ct);
    }

    public static Task PingAsync(CastConnection connection, CancellationToken ct) =>
        connection.SendAsync(
            CastNamespaces.PlatformReceiverId, CastNamespaces.DefaultSenderId, CastNamespaces.Heartbeat,
            new JsonObject { ["type"] = "PING" }, ct);
}
