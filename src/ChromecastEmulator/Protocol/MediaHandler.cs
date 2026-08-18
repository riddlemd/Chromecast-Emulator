using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Device;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

public sealed class MediaHandler : ICastNamespaceHandler
{
    private readonly EmulatorOptions _options;
    private readonly VirtualDevice _device;
    private readonly StatusBroadcaster _broadcaster;
    private readonly CancellationToken _shutdown;
    private readonly ILogger<MediaHandler> _logger;

    public MediaHandler(EmulatorOptions options, VirtualDevice device, StatusBroadcaster broadcaster, CancellationToken shutdown, ILogger<MediaHandler> logger)
    {
        _options = options;
        _device = device;
        _broadcaster = broadcaster;
        _shutdown = shutdown;
        _logger = logger;
    }

    public async Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        var payload = JsonNode.Parse(message.PayloadUtf8)?.AsObject();
        if (payload is null) return;

        var type = payload["type"]?.GetValue<string>();
        var requestId = payload["requestId"]?.GetValue<int>() ?? 0;

        var session = _device.ByTransportId(message.DestinationId);
        if (session is null)
        {
            _logger.LogWarning("media message for unknown transport {TransportId}", message.DestinationId);
            await connection.ReplyAsync(message, Invalid(requestId, "INVALID_REQUEST"), ct);
            return;
        }

        if (type == "LOAD")
        {
            await LoadAsync(connection, message, session, payload, requestId, ct);
            return;
        }

        if (type == "GET_STATUS")
        {
            await connection.ReplyAsync(message, BuildStatus(session, requestId, includeMedia: true), ct);
            return;
        }

        var media = session.Media;
        if (media is null)
        {
            await connection.ReplyAsync(message, Invalid(requestId, "INVALID_MEDIA_SESSION_ID"), ct);
            return;
        }

        // A mediaSessionId is required on every transport command and must match the loaded item.
        if (payload["mediaSessionId"] is { } id
            && id.GetValueKind() == JsonValueKind.Number
            && id.GetValue<int>() != media.MediaSessionId)
        {
            await connection.ReplyAsync(message, Invalid(requestId, "INVALID_MEDIA_SESSION_ID"), ct);
            return;
        }

        switch (type)
        {
            case "PLAY":
                media.Play();
                break;

            case "PAUSE":
                media.Pause();
                break;

            case "SEEK":
                media.Seek(
                    payload["currentTime"]?.GetValue<double>() ?? media.CurrentTime,
                    payload["resumeState"]?.GetValue<string>());
                break;

            case "STOP":
                media.Stop();
                break;

            case "SET_PLAYBACK_RATE":
                media.SetPlaybackRate(payload["playbackRate"]?.GetValue<double>() ?? 1);
                break;

            case "VOLUME":
                // Stream volume mutates the device-global Volume, so receiver-namespace
                // listeners need a RECEIVER_STATUS push too, not just MEDIA_STATUS.
                _device.Volume.Apply(payload["volume"]?.AsObject());
                await _broadcaster.ReceiverStatusAsync(ct);
                break;

            default:
                _logger.LogWarning("unhandled media message '{MessageType}'", type);
                await connection.ReplyAsync(message, Invalid(requestId, "INVALID_COMMAND"), ct);
                return;
        }

        _logger.LogInformation("media {MessageType} -> {PlayerState} @ {CurrentTime:F1}s", type, media.PlayerState, media.CurrentTime);
        await connection.ReplyAsync(message, BuildStatus(session, requestId, includeMedia: false), ct);
        await _broadcaster.MediaStatusAsync(session, includeMedia: false, ct);
    }

    private async Task LoadAsync(
        CastConnection connection, CastMessage message, AppSession session, JsonObject payload, int requestId, CancellationToken ct)
    {
        var media = payload["media"]?.AsObject()?.DeepClone().AsObject();
        if (media is null)
        {
            await connection.ReplyAsync(message, new JsonObject
            {
                ["requestId"] = requestId,
                ["type"] = "LOAD_FAILED",
                ["reason"] = "INVALID_REQUEST",
            }, ct);
            return;
        }

        media["streamType"] ??= "BUFFERED";
        media["contentType"] ??= "application/octet-stream";
        if (media["duration"] is null && _options.DefaultDuration > 0)
            media["duration"] = _options.DefaultDuration;

        var autoplay = payload["autoplay"]?.GetValue<bool>() ?? true;
        var startTime = payload["currentTime"]?.GetValue<double>() ?? 0;

        var mediaSession = session.StartMedia(media, startTime, autoplay, payload["customData"]?.DeepClone());
        mediaSession.Finished += OnFinished;

        _logger.LogInformation("loaded {ContentId} on {TransportId} (mediaSessionId {MediaSessionId})", media["contentId"], session.TransportId, mediaSession.MediaSessionId);

        await connection.ReplyAsync(message, BuildStatus(session, requestId, includeMedia: true), ct);
        await _broadcaster.MediaStatusAsync(session, includeMedia: true, ct);
    }

    private void OnFinished(MediaSession media)
    {
        var session = _device.Sessions.FirstOrDefault(s => s.Media == media);
        if (session is null) return;

        _logger.LogInformation("media finished on {TransportId}", session.TransportId);
        _ = BroadcastFinishedAsync(session);
    }

    /// Fire-and-forget from a timer thread: an unguarded fault here would vanish as an
    /// unobserved task exception with no log line.
    private async Task BroadcastFinishedAsync(AppSession session)
    {
        try
        {
            await _broadcaster.MediaStatusAsync(session, includeMedia: false, _shutdown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FINISHED broadcast failed for {TransportId}", session.TransportId);
        }
    }

    private JsonObject BuildStatus(AppSession session, int requestId, bool includeMedia) =>
        _broadcaster.MediaStatusEnvelope(session, requestId, includeMedia);

    private static JsonObject Invalid(int requestId, string reason) => new()
    {
        ["requestId"] = requestId,
        ["type"] = "INVALID_REQUEST",
        ["reason"] = reason,
    };

}
