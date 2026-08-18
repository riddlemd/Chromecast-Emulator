using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Device;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

public sealed class ReceiverHandler : ICastNamespaceHandler
{
    private readonly VirtualDevice _device;
    private readonly StatusBroadcaster _broadcaster;
    private readonly ILogger<ReceiverHandler> _logger;

    public ReceiverHandler(VirtualDevice device, StatusBroadcaster broadcaster, ILogger<ReceiverHandler> logger)
    {
        _device = device;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        var payload = JsonNode.Parse(message.PayloadUtf8)?.AsObject();
        if (payload is null) return;

        var type = payload["type"]?.GetValue<string>();
        var requestId = payload["requestId"]?.GetValue<int>() ?? 0;

        switch (type)
        {
            case "GET_STATUS":
                await ReplyStatusAsync(connection, message, requestId, ct);
                break;

            case "LAUNCH":
                await LaunchAsync(connection, message, payload, requestId, ct);
                break;

            case "STOP":
                await StopAsync(connection, message, payload, requestId, ct);
                break;

            case "SET_VOLUME":
                _device.Volume.Apply(payload["volume"]?.AsObject());
                await ReplyStatusAsync(connection, message, requestId, ct);
                await _broadcaster.ReceiverStatusAsync(ct);
                break;

            case "GET_APP_AVAILABILITY":
                await ReplyAvailabilityAsync(connection, message, payload, requestId, ct);
                break;

            default:
                _logger.LogWarning("unhandled receiver message '{MessageType}'", type);
                await connection.ReplyAsync(message, new JsonObject
                {
                    ["requestId"] = requestId,
                    ["type"] = "INVALID_REQUEST",
                    ["reason"] = "INVALID_COMMAND",
                }, ct);
                break;
        }
    }

    private async Task LaunchAsync(
        CastConnection connection, CastMessage message, JsonObject payload, int requestId, CancellationToken ct)
    {
        var appId = payload["appId"]?.GetValue<string>();
        if (appId is null)
        {
            await connection.ReplyAsync(message, new JsonObject
            {
                ["requestId"] = requestId,
                ["type"] = "LAUNCH_ERROR",
                ["reason"] = "INVALID_REQUEST",
            }, ct);
            return;
        }

        var session = _device.Launch(appId);
        if (session is null)
        {
            _logger.LogWarning("refused launch of unknown app {AppId} (--strict-apps)", appId);
            await connection.ReplyAsync(message, new JsonObject
            {
                ["requestId"] = requestId,
                ["type"] = "LAUNCH_ERROR",
                ["reason"] = "NOT_FOUND",
            }, ct);
            return;
        }

        _logger.LogInformation("launched {AppId} as session {SessionId} on {TransportId}", appId, session.SessionId, session.TransportId);
        await ReplyStatusAsync(connection, message, requestId, ct);
        await _broadcaster.ReceiverStatusAsync(ct);
    }

    private async Task StopAsync(
        CastConnection connection, CastMessage message, JsonObject payload, int requestId, CancellationToken ct)
    {
        var sessionId = payload["sessionId"]?.GetValue<string>();
        if (sessionId is null || !_device.Stop(sessionId))
        {
            await connection.ReplyAsync(message, new JsonObject
            {
                ["requestId"] = requestId,
                ["type"] = "INVALID_REQUEST",
                ["reason"] = "INVALID_PARAMS",
            }, ct);
            return;
        }

        _logger.LogInformation("stopped session {SessionId}", sessionId);
        await ReplyStatusAsync(connection, message, requestId, ct);
        await _broadcaster.ReceiverStatusAsync(ct);
    }

    private async Task ReplyAvailabilityAsync(
        CastConnection connection, CastMessage message, JsonObject payload, int requestId, CancellationToken ct)
    {
        var availability = new JsonObject();

        foreach (var node in payload["appId"]?.AsArray() ?? [])
        {
            var appId = node?.GetValue<string>();
            if (appId is null) continue;
            availability[appId] = _device.CanLaunch(appId) ? "APP_AVAILABLE" : "APP_UNAVAILABLE";
        }

        await connection.ReplyAsync(message, new JsonObject
        {
            ["requestId"] = requestId,
            ["responseType"] = "GET_APP_AVAILABILITY",
            ["availability"] = availability,
        }, ct);
    }

    private Task ReplyStatusAsync(CastConnection connection, CastMessage message, int requestId, CancellationToken ct) =>
        connection.ReplyAsync(message, _broadcaster.ReceiverStatusEnvelope(requestId), ct);
}
