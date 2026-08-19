using System.Text.Json.Nodes;
using ChromecastEmulator.Device;
using ChromecastEmulator.Render;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Render;

public class RenderControllerTests
{
    /// Records every command RenderController posts and can raise Ready on demand, standing
    /// in for the real webview — which cannot be constructed in tests (see RenderWindow: it
    /// would open a real window and crash off the main thread).
    private sealed class RecordingRenderSurface : IRenderSurface
    {
        private readonly List<JsonObject> _posted = [];
        private readonly Lock _gate = new();

        public event Action? Ready;

        public IReadOnlyList<JsonObject> Posted
        {
            get { lock (_gate) return _posted.ToArray(); }
        }

        public void Post(string json)
        {
            lock (_gate) _posted.Add(JsonNode.Parse(json)!.AsObject());
        }

        public void RaiseReady() => Ready?.Invoke();
    }

    private sealed class Sut : IDisposable
    {
        public required HlsPipeline Pipeline { get; init; }
        public required RecordingRenderSurface Surface { get; init; }
        public required RenderController Controller { get; init; }

        public void Dispose()
        {
            Controller.Dispose();
            Pipeline.Dispose();
        }
    }

    private static Sut CreateSut(TempState temp)
    {
        var stub = FfmpegStub.Create(temp.Directory);
        var options = temp.Options(o => o.FfmpegPath = stub);
        var pipeline = new HlsPipeline(options, Path.Combine(temp.Directory, "output"), NullLogger<HlsPipeline>.Instance);
        var surface = new RecordingRenderSurface();
        var device = new VirtualDevice(options, "test-device");
        var controller = new RenderController(pipeline, surface, device, NullLogger<RenderController>.Instance);
        return new Sut { Pipeline = pipeline, Surface = surface, Controller = controller };
    }

    private static string? Cmd(JsonObject command) => command["cmd"]?.GetValue<string>();

    [Fact]
    public void Load_NoContentId_PostsNothingAndDoesNotThrow()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1, new JsonObject(), 0, autoplay: false, null);

        sut.Controller.Load(media);

        Assert.Empty(sut.Surface.Posted);
    }

    [Fact]
    public async Task Load_WithContentId_PostsLoadingImmediatelyThenLoadOnceReady()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1,
            new JsonObject { ["contentId"] = "fast", ["metadata"] = new JsonObject { ["title"] = "My Movie" } },
            10, autoplay: false, null);

        sut.Controller.Load(media);

        var loading = Assert.Single(sut.Surface.Posted);
        Assert.Equal("loading", Cmd(loading));
        Assert.Equal("My Movie", loading["title"]!.GetValue<string>());

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));
        var load = sut.Surface.Posted.First(c => Cmd(c) == "load");
        Assert.Contains(HlsPipeline.PlaylistName, load["src"]!.GetValue<string>());
        Assert.Equal("My Movie", load["title"]!.GetValue<string>());
        Assert.Equal(10, load["currentTime"]!.GetValue<double>());
    }

    [Fact]
    public async Task Load_ContentUrlOnly_UsesItAsTheSource()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        // Senders on the modern CAF shape send contentUrl and no contentId at all; reading
        // only contentId silently rendered nothing.
        using var media = new MediaSession(1, new JsonObject { ["contentUrl"] = "fast" }, 0, autoplay: false, null);

        sut.Controller.Load(media);

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));
        Assert.Equal("fast", sut.Surface.Posted.First(c => Cmd(c) == "load")["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Load_BothContentUrlAndContentId_PrefersContentUrl()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        // contentId is an opaque identifier when contentUrl is present, so transcoding it
        // would feed ffmpeg something that is not a URL at all.
        using var media = new MediaSession(1,
            new JsonObject { ["contentUrl"] = "fast", ["contentId"] = "silent" }, 0, autoplay: false, null);

        sut.Controller.Load(media);

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));
        Assert.Equal("fast", sut.Surface.Posted.First(c => Cmd(c) == "load")["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Load_SupersededWhileTranscoding_DoesNotPostTheAbandonedLoad()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var abandoned = new MediaSession(1, new JsonObject { ["contentId"] = "silent" }, 0, autoplay: false, null);
        using var current = new MediaSession(2, new JsonObject { ["contentId"] = "fast" }, 0, autoplay: false, null);

        sut.Controller.Load(abandoned);
        sut.Controller.Load(current);

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));
        await Task.Delay(300);
        // The first load's detached wait must not deliver its own load command afterwards.
        Assert.Single(sut.Surface.Posted, c => Cmd(c) == "load");
    }

    [Fact]
    public async Task Load_NoMetadataTitle_FallsBackToContentId()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1, new JsonObject { ["contentId"] = "fast" }, 0, autoplay: false, null);

        sut.Controller.Load(media);

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));
        var load = sut.Surface.Posted.First(c => Cmd(c) == "load");
        Assert.Equal("fast", load["title"]!.GetValue<string>());
    }

    [Fact]
    public void Sync_CurrentSession_PostsSyncCommand()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1, new JsonObject { ["contentId"] = "fast" }, 5, autoplay: false, null);
        sut.Controller.Load(media);

        sut.Controller.Sync(media);

        var sync = sut.Surface.Posted.Last();
        Assert.Equal("sync", Cmd(sync));
        Assert.Equal("PAUSED", sync["state"]!.GetValue<string>());
        Assert.Equal(5, sync["currentTime"]!.GetValue<double>());
        Assert.Equal(1, sync["rate"]!.GetValue<double>());
    }

    [Fact]
    public void Sync_SessionNoLongerCurrent_PostsNothingNew()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var first = new MediaSession(1, new JsonObject { ["contentId"] = "fast" }, 0, autoplay: false, null);
        using var second = new MediaSession(2, new JsonObject { ["contentId"] = "fast" }, 0, autoplay: false, null);
        sut.Controller.Load(first);
        sut.Controller.Load(second);
        var countBeforeSync = sut.Surface.Posted.Count;

        sut.Controller.Sync(first);

        Assert.Equal(countBeforeSync, sut.Surface.Posted.Count);
    }

    [Fact]
    public void SetVolume_PostsLevelAndMuted()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);

        sut.Controller.SetVolume(new Volume { Level = 0.5, Muted = true });

        var volume = sut.Surface.Posted.Last();
        Assert.Equal("volume", Cmd(volume));
        Assert.Equal(0.5, volume["level"]!.GetValue<double>());
        Assert.True(volume["muted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Clear_StopsTheTranscode()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1, new JsonObject { ["contentId"] = "silent" }, 0, autoplay: false, null);
        var pidFile = Path.Combine(temp.Directory, "output", "ffmpeg.pid");
        sut.Controller.Load(media);
        Assert.True(await Poll.UntilAsync(() => File.Exists(pidFile), TimeSpan.FromSeconds(3)));
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());

        sut.Controller.Clear();

        // Blanking the window while ffmpeg keeps transcoding leaks a process per stopped
        // session and keeps burning CPU on media nobody is watching.
        Assert.True(await Poll.UntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(3)));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void Clear_PostsIdle()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);

        sut.Controller.Clear();

        var idle = sut.Surface.Posted.Last();
        Assert.Equal("idle", Cmd(idle));
    }

    [Fact]
    public void Ready_BeforeAnyLoad_PostsIdle()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);

        sut.Surface.RaiseReady();

        var idle = Assert.Single(sut.Surface.Posted);
        Assert.Equal("idle", Cmd(idle));
    }

    [Fact]
    public void Ready_WhileTranscoding_PostsLoading()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1,
            new JsonObject { ["contentId"] = "silent", ["metadata"] = new JsonObject { ["title"] = "Still Loading" } },
            0, autoplay: false, null);
        sut.Controller.Load(media);

        sut.Surface.RaiseReady();

        // Load already posted one "loading"; asserting on Last() alone would pass even if
        // Ready answered a reloaded page with nothing at all.
        Assert.Equal(2, sut.Surface.Posted.Count);
        var loading = sut.Surface.Posted[1];
        Assert.Equal("loading", Cmd(loading));
        Assert.Equal("Still Loading", loading["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ready_AfterPlaylistReady_PostsLoad()
    {
        using var temp = new TempState();
        using var sut = CreateSut(temp);
        using var media = new MediaSession(1, new JsonObject { ["contentId"] = "fast" }, 0, autoplay: false, null);
        sut.Controller.Load(media);
        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Any(c => Cmd(c) == "load"), TimeSpan.FromSeconds(3)));

        sut.Surface.RaiseReady();

        Assert.True(await Poll.UntilAsync(() => sut.Surface.Posted.Count(c => Cmd(c) == "load") >= 2, TimeSpan.FromSeconds(2)));
    }
}
