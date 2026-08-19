using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Device;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Render;

/// Drives the render window from media state.
///
/// Commands are derived from the session's state rather than from the Cast message that
/// caused it, so a resend is always safe — which is what makes recovering after a page
/// reload just a matter of replaying the current state.
public sealed class RenderController : IMediaRenderer, IDisposable
{
    private readonly HlsPipeline _pipeline;
    private readonly IRenderSurface _window;
    private readonly VirtualDevice _device;
    private readonly ILogger<RenderController> _logger;
    private readonly Lock _gate = new();

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    private MediaSession? _current;
    private string? _playlistUrl;
    private bool _ready;

    public RenderController(
        HlsPipeline pipeline, IRenderSurface window, VirtualDevice device, ILogger<RenderController> logger)
    {
        _pipeline = pipeline;
        _window = window;
        _device = device;
        _logger = logger;

        _window.Ready += OnReady;
    }

    public void Load(MediaSession media)
    {
        var source = SourceUrl(media.Media);
        if (source is null)
        {
            _logger.LogWarning("render skipped: LOAD had neither contentUrl nor contentId");
            return;
        }

        string? playlistUrl;
        int generation;
        lock (_gate)
        {
            _current = media;
            _ready = false;
            playlistUrl = _playlistUrl = _pipeline.Start(source);
            generation = _pipeline.Generation;
        }

        if (playlistUrl is null)
        {
            Send(new JsonObject { ["cmd"] = "error", ["text"] = "ffmpeg could not start" });
            return;
        }

        Send(new JsonObject { ["cmd"] = "loading", ["title"] = Title(media) });
        _ = ShowWhenReadyAsync(media, playlistUrl, generation);
    }

    /// LOAD has to be answered immediately, so the wait for ffmpeg's first segment runs
    /// detached and only then points the page at the playlist.
    private async Task ShowWhenReadyAsync(MediaSession media, string playlistUrl, int generation)
    {
        try
        {
            var ready = await _pipeline.WaitForFirstSegmentAsync(generation, StartupTimeout, CancellationToken.None);

            lock (_gate)
            {
                // Another LOAD landed while ffmpeg was starting up.
                if (!ReferenceEquals(_current, media)) return;
                _ready = ready;
            }

            if (!ready)
            {
                _logger.LogWarning("render gave up waiting for ffmpeg output");
                Send(new JsonObject { ["cmd"] = "error", ["text"] = "ffmpeg produced no output" });
                return;
            }

            SendLoad(media, playlistUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("render load failed: {Reason}", ex.Message);
        }
    }

    private void SendLoad(MediaSession media, string playlistUrl)
    {
        Send(new JsonObject
        {
            ["cmd"] = "load",
            ["src"] = playlistUrl,
            ["title"] = Title(media),
            ["autoplay"] = media.PlayerState == "PLAYING",
            ["currentTime"] = Math.Round(media.CurrentTime, 3),
        });
        SetVolume(_device.Volume);
    }

    public void Sync(MediaSession media)
    {
        lock (_gate)
        {
            // A stale session can still emit state after being replaced by the next LOAD.
            if (!ReferenceEquals(_current, media)) return;
        }

        Send(new JsonObject
        {
            ["cmd"] = "sync",
            ["state"] = media.PlayerState,
            ["currentTime"] = Math.Round(media.CurrentTime, 3),
            ["rate"] = media.PlaybackRate,
        });
    }

    public void SetVolume(Volume volume) =>
        Send(new JsonObject
        {
            ["cmd"] = "volume",
            ["level"] = volume.Level,
            ["muted"] = volume.Muted,
        });

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
            _playlistUrl = null;
            _ready = false;
        }

        _pipeline.Stop();
        Send(new JsonObject { ["cmd"] = "idle" });
    }

    /// The page came up (or reloaded) with no state of its own.
    private void OnReady()
    {
        MediaSession? media;
        string? playlistUrl;
        bool ready;
        lock (_gate)
        {
            media = _current;
            playlistUrl = _playlistUrl;
            ready = _ready;
        }

        if (media is null || playlistUrl is null)
        {
            Send(new JsonObject { ["cmd"] = "idle" });
            return;
        }

        if (!ready)
        {
            // Still transcoding; ShowWhenReadyAsync will send the real load command.
            Send(new JsonObject { ["cmd"] = "loading", ["title"] = Title(media) });
            return;
        }

        SendLoad(media, playlistUrl);
    }

    private static string Title(MediaSession media)
    {
        var metadata = media.Media["metadata"]?.AsObject();
        return Text(metadata?["title"]) ?? SourceUrl(media.Media) ?? "";
    }

    /// Newer senders carry the playable URL in `contentUrl` and leave `contentId` as an
    /// opaque identifier; older ones put the URL in `contentId`. Either is valid CAF, so
    /// the window has to accept both.
    private static string? SourceUrl(JsonObject media) =>
        Text(media["contentUrl"]) ?? Text(media["contentId"]);

    private static string? Text(JsonNode? node)
    {
        if (node?.GetValueKind() != JsonValueKind.String) return null;
        var value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void Send(JsonObject command) => _window.Post(command.ToJsonString(JsonSerializerOptions.Default));

    public void Dispose()
    {
        _window.Ready -= OnReady;
        _pipeline.Stop();
    }
}
