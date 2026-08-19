using ChromecastEmulator.Device;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.UnitTests.Device;

public class MediaSessionTests
{
    private static JsonObject MediaWithDuration(double duration) => new() { ["duration"] = duration };

    [Fact]
    public void Constructor_Autoplay_StartsPlaying()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        Assert.Equal("PLAYING", media.PlayerState);
    }

    [Fact]
    public void Constructor_NoAutoplay_StartsPaused()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        Assert.Equal("PAUSED", media.PlayerState);
    }

    [Fact]
    public void CurrentTime_StartTime_IsReflected()
    {
        using var media = new MediaSession(1, [], 42, autoplay: false, null);

        Assert.Equal(42, media.CurrentTime);
    }

    [Fact]
    public void Duration_KeyAbsent_IsNull()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        Assert.Null(media.Duration);
    }

    [Fact]
    public void Duration_NonNumericValue_IsNull()
    {
        using var media = new MediaSession(1, new JsonObject { ["duration"] = "not-a-number" }, 0, autoplay: false, null);

        Assert.Null(media.Duration);
    }

    [Fact]
    public void Duration_NumericValue_IsReturned()
    {
        using var media = new MediaSession(1, MediaWithDuration(120), 0, autoplay: false, null);

        Assert.Equal(120, media.Duration);
    }

    [Fact]
    public void CurrentTime_NoDuration_FlooredAtZero()
    {
        using var media = new MediaSession(1, [], -5, autoplay: false, null);

        Assert.Equal(0, media.CurrentTime);
    }

    [Fact]
    public void CurrentTime_WithDuration_ClampedToDuration()
    {
        using var media = new MediaSession(1, MediaWithDuration(10), 999, autoplay: false, null);

        Assert.Equal(10, media.CurrentTime);
    }

    [Fact]
    public void CurrentTime_WithDuration_ClampedAtZero()
    {
        using var media = new MediaSession(1, MediaWithDuration(10), -5, autoplay: false, null);

        Assert.Equal(0, media.CurrentTime);
    }

    [Fact]
    public void Pause_AfterStop_LeavesSessionIdle()
    {
        using var media = new MediaSession(1, [], 5, autoplay: true, null);
        media.Stop();

        media.Pause();

        // Without Pause's state guard a stopped session would come back as PAUSED, and the
        // sender would see a finished item as merely paused.
        Assert.Equal("IDLE", media.PlayerState);
        Assert.Equal("CANCELLED", media.IdleReason);
    }

    [Fact]
    public async Task Play_AlreadyPlaying_DoesNotRestartTheClock()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);
        await Task.Delay(150);
        var elapsed = media.CurrentTime;

        media.Play();

        // A second Play must not restart the stopwatch; that would silently rewind
        // currentTime to zero while the session stayed PLAYING.
        Assert.Equal("PLAYING", media.PlayerState);
        Assert.True(media.CurrentTime >= elapsed, $"currentTime went backwards: {media.CurrentTime} < {elapsed}");
    }

    [Fact]
    public async Task Pause_WhilePlaying_FreezesCurrentTime()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        await Task.Delay(150);
        media.Pause();
        var frozen = media.CurrentTime;
        await Task.Delay(150);

        Assert.Equal("PAUSED", media.PlayerState);
        Assert.Equal(frozen, media.CurrentTime, precision: 6);
    }

    [Fact]
    public void Seek_ResetsIdleReason()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);
        media.Stop();

        media.Seek(5, null);

        Assert.Null(media.IdleReason);
    }

    [Fact]
    public void Seek_ResumeStatePlaybackStart_StartsPlaying()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        media.Seek(5, "PLAYBACK_START");

        Assert.Equal("PLAYING", media.PlayerState);
    }

    [Fact]
    public void Seek_ResumeStatePlaybackPause_StaysPaused()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        media.Seek(5, "PLAYBACK_PAUSE");

        Assert.Equal("PAUSED", media.PlayerState);
    }

    [Fact]
    public void Seek_ResumeStateNull_KeepsCurrentState()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        media.Seek(5, null);

        Assert.Equal("PLAYING", media.PlayerState);
    }

    [Fact]
    public void Seek_ResumeStateNull_WhilePaused_StaysPaused()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        media.Seek(5, null);

        Assert.Equal("PAUSED", media.PlayerState);
    }

    [Fact]
    public void Stop_DefaultReason_SetsIdleAndCancelled()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        media.Stop();

        Assert.Equal("IDLE", media.PlayerState);
        Assert.Equal("CANCELLED", media.IdleReason);
    }

    [Fact]
    public void Stop_SuppliedReason_SetsIdleReason()
    {
        using var media = new MediaSession(1, [], 0, autoplay: true, null);

        media.Stop("ERROR");

        Assert.Equal("ERROR", media.IdleReason);
    }

    [Fact]
    public void SetPlaybackRate_UpdatesPlaybackRate()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        media.SetPlaybackRate(2.0);

        Assert.Equal(2.0, media.PlaybackRate);
    }

    [Fact]
    public void SetPlaybackRate_WhilePaused_DoesNotStartTheClock()
    {
        using var media = new MediaSession(1, [], 5, autoplay: false, null);

        media.SetPlaybackRate(2.0);
        Thread.Sleep(50);

        Assert.Equal("PAUSED", media.PlayerState);
        Assert.Equal(5, media.CurrentTime, precision: 3);
    }

    [Fact]
    public void BuildStatus_IncludeMediaTrue_IncludesMediaKey()
    {
        using var media = new MediaSession(1, MediaWithDuration(10), 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume(), includeMedia: true);

        Assert.NotNull(status["media"]);
    }

    [Fact]
    public void BuildStatus_IncludeMediaFalse_OmitsMediaKey()
    {
        using var media = new MediaSession(1, MediaWithDuration(10), 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume(), includeMedia: false);

        Assert.False(status.ContainsKey("media"));
    }

    [Fact]
    public void BuildStatus_BeforeStop_OmitsIdleReason()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume());

        Assert.False(status.ContainsKey("idleReason"));
    }

    [Fact]
    public void BuildStatus_AfterStop_IncludesIdleReason()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);
        media.Stop();

        var status = media.BuildStatus(new Volume());

        Assert.Equal("CANCELLED", status["idleReason"]!.GetValue<string>());
    }

    [Fact]
    public void BuildStatus_NoCustomData_OmitsCustomDataKey()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume());

        Assert.False(status.ContainsKey("customData"));
    }

    [Fact]
    public void BuildStatus_WithCustomData_IncludesCustomDataKey()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, new JsonObject { ["foo"] = "bar" });

        var status = media.BuildStatus(new Volume());

        Assert.Equal("bar", status["customData"]!["foo"]!.GetValue<string>());
    }

    [Fact]
    public void BuildStatus_MediaSessionIdAndCurrentItemId_Match()
    {
        using var media = new MediaSession(7, [], 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume());

        Assert.Equal(7, status["mediaSessionId"]!.GetValue<int>());
        Assert.Equal(7, status["currentItemId"]!.GetValue<int>());
    }

    [Fact]
    public void BuildStatus_SupportedMediaCommands_Is274447()
    {
        using var media = new MediaSession(1, [], 0, autoplay: false, null);

        var status = media.BuildStatus(new Volume());

        Assert.Equal(274447, status["supportedMediaCommands"]!.GetValue<int>());
    }

    [Fact]
    public async Task Finished_ShortDurationWithAutoplay_FiresAndBecomesIdle()
    {
        using var media = new MediaSession(1, MediaWithDuration(0.1), 0, autoplay: true, null);
        var tcs = new TaskCompletionSource();
        media.Finished += _ => tcs.TrySetResult();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(tcs.Task, completed);
        Assert.Equal("IDLE", media.PlayerState);
        Assert.Equal("FINISHED", media.IdleReason);
    }
}
