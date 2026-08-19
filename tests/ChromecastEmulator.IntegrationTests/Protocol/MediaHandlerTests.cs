using System.Text.Json.Nodes;
using ChromecastEmulator;
using ChromecastEmulator.Device;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Render;
using ChromecastEmulator.Transport;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Protocol;

public class MediaHandlerTests
{
    private static (VirtualDevice Device, EmulatorOptions Options, MediaHandler Handler) CreateSut(
        Action<EmulatorOptions>? configure = null, IMediaRenderer? renderer = null)
    {
        var options = new EmulatorOptions();
        configure?.Invoke(options);
        var device = new VirtualDevice(options, "abc");
        // No registered connections, so broadcasts cannot add frames the assertions would misread.
        var registry = Substitute.For<IConnectionRegistry>();
        registry.Connections.Returns([]);
        var broadcaster = new StatusBroadcaster(registry, device);
        var handler = new MediaHandler(options, device, broadcaster, CancellationToken.None, NullLogger<MediaHandler>.Instance, renderer);
        return (device, options, handler);
    }

    private static JsonObject FirstStatus(CastMessage reply) =>
        JsonNode.Parse(reply.PayloadUtf8)!.AsObject()["status"]!.AsArray()[0]!.AsObject();

    [Fact]
    public async Task HandleAsync_Volume_BroadcastsReceiverStatusToPlatformListeners()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var options = new EmulatorOptions();
        var device = new VirtualDevice(options, "abc");
        var registry = Substitute.For<IConnectionRegistry>();
        registry.Connections.Returns([channel.Receiver]);
        var broadcaster = new StatusBroadcaster(registry, device);
        var handler = new MediaHandler(options, device, broadcaster, CancellationToken.None, NullLogger<MediaHandler>.Instance);
        var session = device.Launch("CC1AD845")!;
        session.StartMedia([], 0, autoplay: true, null);
        channel.Receiver.Open(new VirtualConnection(CastNamespaces.DefaultSenderId, CastNamespaces.PlatformReceiverId));

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"VOLUME","requestId":7,"volume":{"level":0.25}}""",
            destination: session.TransportId), CancellationToken.None);

        // Stream volume mutates the device-global Volume; platform-connected senders
        // must hear about it without polling.
        var broadcast = await channel.ReadUntilAsync(m =>
            m.Namespace == CastNamespaces.Receiver
            && JsonNode.Parse(m.PayloadUtf8)!["type"]!.GetValue<string>() == "RECEIVER_STATUS");

        var level = JsonNode.Parse(broadcast.PayloadUtf8)!["status"]!["volume"]!["level"]!.GetValue<double>();
        Assert.Equal(0.25, level, precision: 4);
        Assert.Equal(0.25, device.Volume.Level);
    }

    [Fact]
    public async Task HandleAsync_UnknownTransportId_RepliesInvalidRequest()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, _, handler) = CreateSut();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media, """{"type":"GET_STATUS","requestId":1}""", destination: "web-99"), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("INVALID_REQUEST", payload["type"]!.GetValue<string>());
        Assert.Equal("INVALID_REQUEST", payload["reason"]!.GetValue<string>());
        Assert.Equal(1, payload["requestId"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"type":"LOAD","requestId":11,"media":{"contentId":"x"}}""", 11)]
    [InlineData("""{"type":"GET_STATUS","requestId":12}""", 12)]
    [InlineData("""{"type":"PLAY","requestId":13,"mediaSessionId":1}""", 13)]
    [InlineData("""{"type":"PAUSE","requestId":14,"mediaSessionId":1}""", 14)]
    [InlineData("""{"type":"SEEK","requestId":15,"mediaSessionId":1,"currentTime":2}""", 15)]
    [InlineData("""{"type":"SET_PLAYBACK_RATE","requestId":16,"mediaSessionId":1,"playbackRate":2}""", 16)]
    public async Task HandleAsync_AnyMediaCommand_EchoesTheRequestId(string request, int expected)
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;
        session.StartMedia([], 0, autoplay: true, null);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media, request,
            destination: session.TransportId), CancellationToken.None);
        var reply = await channel.ReadAsync();

        // Senders correlate replies by requestId and hang without it.
        Assert.Equal(expected, JsonNode.Parse(reply.PayloadUtf8)!["requestId"]!.GetValue<int>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Load_CreatesMediaSessionAndRepliesMediaStatus()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x","contentType":"video/mp4"}}""",
            destination: session.TransportId), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var status = FirstStatus(reply);
        Assert.True(status["mediaSessionId"]!.GetValue<int>() > 0);
        Assert.Equal("x", status["media"]!["contentId"]!.GetValue<string>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Load_HonoursCurrentTimeAndAutoplay()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x","duration":100},"currentTime":30,"autoplay":false}""",
            destination: session.TransportId), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var status = FirstStatus(reply);
        Assert.Equal("PAUSED", status["playerState"]!.GetValue<string>());
        Assert.Equal(30, status["currentTime"]!.GetValue<double>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Load_DefaultDurationAppliedOnlyWhenDurationOmitted()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut(o => o.DefaultDuration = 120);
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        var withoutDuration = FirstStatus(await channel.ReadAsync());
        Assert.Equal(120, withoutDuration["media"]!["duration"]!.GetValue<double>());

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":2,"media":{"contentId":"y","duration":50}}""",
            destination: session.TransportId), CancellationToken.None);
        var withDuration = FirstStatus(await channel.ReadAsync());
        Assert.Equal(50, withDuration["media"]!["duration"]!.GetValue<double>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_GetStatus_ReturnsCurrentMediaStatus()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();
        var mediaSessionId = session.Media!.MediaSessionId;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"GET_STATUS","requestId":2}""",
            destination: session.TransportId), CancellationToken.None);
        var status = FirstStatus(await channel.ReadAsync());

        Assert.Equal(mediaSessionId, status["mediaSessionId"]!.GetValue<int>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_PlaybackCommands_MovePlayerStateAsExpected()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x","duration":100}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"PAUSE","requestId":2}""", destination: session.TransportId), CancellationToken.None);
        Assert.Equal("PAUSED", FirstStatus(await channel.ReadAsync())["playerState"]!.GetValue<string>());

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"PLAY","requestId":3}""", destination: session.TransportId), CancellationToken.None);
        Assert.Equal("PLAYING", FirstStatus(await channel.ReadAsync())["playerState"]!.GetValue<string>());

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"SEEK","requestId":4,"currentTime":42}""", destination: session.TransportId), CancellationToken.None);
        var seeked = FirstStatus(await channel.ReadAsync());
        Assert.Equal(42, seeked["currentTime"]!.GetValue<double>());
        Assert.Equal("PLAYING", seeked["playerState"]!.GetValue<string>());

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"STOP","requestId":5}""", destination: session.TransportId), CancellationToken.None);
        var stopped = FirstStatus(await channel.ReadAsync());
        Assert.Equal("IDLE", stopped["playerState"]!.GetValue<string>());
        Assert.Equal("CANCELLED", stopped["idleReason"]!.GetValue<string>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_SetPlaybackRate_UpdatesPlaybackRate()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x","duration":100}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"SET_PLAYBACK_RATE","requestId":2,"playbackRate":2}""", destination: session.TransportId), CancellationToken.None);
        var status = FirstStatus(await channel.ReadAsync());

        Assert.Equal(2, status["playbackRate"]!.GetValue<double>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_UnhandledMediaMessageType_DoesNotThrow()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (device, _, handler) = CreateSut();
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"BOGUS","requestId":2}""", destination: session.TransportId), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("INVALID_REQUEST", payload["type"]!.GetValue<string>());
        Assert.Equal("INVALID_COMMAND", payload["reason"]!.GetValue<string>());

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Load_CallsRendererLoad()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var renderer = Substitute.For<IMediaRenderer>();
        var (device, _, handler) = CreateSut(renderer: renderer);
        var session = device.Launch("CC1AD845")!;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        renderer.Received(1).Load(session.Media!);

        session.Dispose();
    }

    [Theory]
    [InlineData("""{"type":"PLAY","requestId":2}""")]
    [InlineData("""{"type":"PAUSE","requestId":2}""")]
    [InlineData("""{"type":"SEEK","requestId":2,"currentTime":5}""")]
    public async Task HandleAsync_TransportCommand_CallsRendererSync(string commandJson)
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var renderer = Substitute.For<IMediaRenderer>();
        var (device, _, handler) = CreateSut(renderer: renderer);
        var session = device.Launch("CC1AD845")!;
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x","duration":100}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();
        renderer.ClearReceivedCalls();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            commandJson, destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        renderer.Received(1).Sync(session.Media!);

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Stop_CallsRendererClear()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var renderer = Substitute.For<IMediaRenderer>();
        var (device, _, handler) = CreateSut(renderer: renderer);
        var session = device.Launch("CC1AD845")!;
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"STOP","requestId":2}""", destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        renderer.Received(1).Clear();

        session.Dispose();
    }

    [Fact]
    public async Task HandleAsync_Volume_DoesNotCallSyncOrSetVolumeOnRenderer()
    {
        // Volume.Changed reaches the renderer on its own now (wired outside MediaHandler);
        // MediaHandler must not double-drive it.
        await using var channel = await LoopbackChannel.CreateAsync();
        var renderer = Substitute.For<IMediaRenderer>();
        var (device, _, handler) = CreateSut(renderer: renderer);
        var session = device.Launch("CC1AD845")!;
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"LOAD","requestId":1,"media":{"contentId":"x"}}""",
            destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();
        renderer.ClearReceivedCalls();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Media,
            """{"type":"VOLUME","requestId":2,"volume":{"level":0.3}}""", destination: session.TransportId), CancellationToken.None);
        await channel.ReadAsync();

        renderer.DidNotReceive().Sync(Arg.Any<MediaSession>());
        renderer.DidNotReceive().SetVolume(Arg.Any<Volume>());

        session.Dispose();
    }
}
