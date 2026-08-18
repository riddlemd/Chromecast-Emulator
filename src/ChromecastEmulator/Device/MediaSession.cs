using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.Device;

public sealed class MediaSession : IDisposable
{
    /// PAUSE | SEEK | STREAM_VOLUME | STREAM_MUTE | SKIP_FWD | SKIP_BACK | QUEUE_NEXT | QUEUE_PREV, as a real receiver reports it.
    private const int SupportedMediaCommands = 274447;

    private readonly Stopwatch _clock = new();
    private readonly Timer _completionTimer;
    private readonly Lock _gate = new();
    private double _baseTime;
    private bool _disposed;

    public MediaSession(int mediaSessionId, JsonObject media, double startTime, bool autoplay, JsonNode? customData)
    {
        MediaSessionId = mediaSessionId;
        Media = media;
        CustomData = customData;
        _baseTime = startTime;
        PlayerState = autoplay ? "PLAYING" : "PAUSED";
        if (autoplay) _clock.Start();

        _completionTimer = new Timer(_ => CheckCompletion(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public int MediaSessionId { get; }
    public JsonObject Media { get; }
    public JsonNode? CustomData { get; }
    public string PlayerState { get; private set; }
    public string? IdleReason { get; private set; }
    public double PlaybackRate { get; private set; } = 1;

    public event Action<MediaSession>? Finished;

    public double? Duration =>
        Media["duration"] is { } node && node.GetValueKind() is System.Text.Json.JsonValueKind.Number
            ? node.GetValue<double>()
            : null;

    public double CurrentTime
    {
        get
        {
            lock (_gate)
            {
                var elapsed = _baseTime + _clock.Elapsed.TotalSeconds * PlaybackRate;
                return Duration is { } d ? Math.Clamp(elapsed, 0, d) : Math.Max(0, elapsed);
            }
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (PlayerState == "PLAYING") return;
            PlayerState = "PLAYING";
            IdleReason = null;
            _clock.Restart();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (PlayerState != "PLAYING") return;
            _baseTime += _clock.Elapsed.TotalSeconds * PlaybackRate;
            _clock.Reset();
            PlayerState = "PAUSED";
        }
    }

    public void Seek(double seconds, string? resumeState)
    {
        lock (_gate)
        {
            _baseTime = Duration is { } d ? Math.Clamp(seconds, 0, d) : Math.Max(0, seconds);
            _clock.Reset();

            var shouldPlay = resumeState switch
            {
                "PLAYBACK_START" => true,
                "PLAYBACK_PAUSE" => false,
                _ => PlayerState == "PLAYING",
            };

            PlayerState = shouldPlay ? "PLAYING" : "PAUSED";
            IdleReason = null;
            if (shouldPlay) _clock.Restart();
        }
    }

    public void SetPlaybackRate(double rate)
    {
        lock (_gate)
        {
            _baseTime += _clock.Elapsed.TotalSeconds * PlaybackRate;
            // Restart would start the clock on a PAUSED session, advancing time while paused.
            if (PlayerState == "PLAYING") _clock.Restart();
            else _clock.Reset();
            PlaybackRate = rate;
        }
    }

    public void Stop(string reason = "CANCELLED")
    {
        lock (_gate)
        {
            _clock.Reset();
            PlayerState = "IDLE";
            IdleReason = reason;
        }
    }

    public JsonObject BuildStatus(Volume volume, bool includeMedia = true)
    {
        var status = new JsonObject
        {
            ["mediaSessionId"] = MediaSessionId,
            ["playbackRate"] = PlaybackRate,
            ["playerState"] = PlayerState,
            ["currentTime"] = Math.Round(CurrentTime, 3),
            ["supportedMediaCommands"] = SupportedMediaCommands,
            ["volume"] = volume.ToJson(),
            ["currentItemId"] = MediaSessionId,
            ["repeatMode"] = "REPEAT_OFF",
        };

        // A real receiver omits `media` from incremental status updates and only
        // resends it after LOAD; senders cache it against mediaSessionId.
        if (includeMedia) status["media"] = Media.DeepClone();
        if (IdleReason is not null) status["idleReason"] = IdleReason;
        if (CustomData is not null) status["customData"] = CustomData.DeepClone();

        return status;
    }

    private void CheckCompletion()
    {
        bool finished;
        lock (_gate)
        {
            // Timer.Dispose does not wait for an in-flight tick; without this a replaced
            // session could still fire Finished.
            if (_disposed || PlayerState != "PLAYING" || Duration is not { } duration) return;
            finished = _baseTime + _clock.Elapsed.TotalSeconds * PlaybackRate >= duration;
            if (finished)
            {
                _baseTime = duration;
                _clock.Reset();
                PlayerState = "IDLE";
                IdleReason = "FINISHED";
            }
        }

        if (finished) Finished?.Invoke(this);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _completionTimer.Dispose();
    }
}
