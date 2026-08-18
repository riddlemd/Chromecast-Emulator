using System.Text.Json.Nodes;
using ChromecastEmulator;
using ChromecastEmulator.Device;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Transport;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Protocol;

public class ReceiverHandlerTests
{
    private static (LoopbackChannel Channel, VirtualDevice Device, EmulatorOptions Options, ReceiverHandler Handler) CreateSut(LoopbackChannel channel, Action<EmulatorOptions>? configure = null)
    {
        var options = new EmulatorOptions();
        configure?.Invoke(options);
        var device = new VirtualDevice(options, "abc");
        // Empty registry, same as MediaHandlerTests: these tests assert on direct replies,
        // and a broadcast frame on the same stream would poison later ReadAsync calls.
        var registry = Substitute.For<IConnectionRegistry>();
        registry.Connections.Returns([]);
        var broadcaster = new StatusBroadcaster(registry, device);
        var handler = new ReceiverHandler(device, broadcaster, NullLogger<ReceiverHandler>.Instance);
        return (channel, device, options, handler);
    }

    [Fact]
    public async Task HandleAsync_GetStatus_RepliesReceiverStatusWithMatchingRequestIdAndApplications()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, _, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"GET_STATUS","requestId":1}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("RECEIVER_STATUS", payload["type"]!.GetValue<string>());
        Assert.Equal(1, payload["requestId"]!.GetValue<int>());
        Assert.NotNull(payload["status"]!["applications"]);
    }

    [Fact]
    public async Task HandleAsync_GetStatusWhileIdle_ReportsBackdropAppWithIsIdleScreenTrue()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, _, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"GET_STATUS","requestId":1}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var applications = JsonNode.Parse(reply.PayloadUtf8)!.AsObject()["status"]!["applications"]!.AsArray();
        Assert.Single(applications);
        Assert.Equal("E8C28D3C", applications[0]!["appId"]!.GetValue<string>());
        Assert.True(applications[0]!["isIdleScreen"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HandleAsync_Launch_StartsSessionAndDeviceNoLongerIdle()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, device, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"LAUNCH","requestId":2,"appId":"CC1AD845"}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var applications = JsonNode.Parse(reply.PayloadUtf8)!.AsObject()["status"]!["applications"]!.AsArray();
        Assert.Single(applications);
        Assert.False(string.IsNullOrEmpty(applications[0]!["sessionId"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(applications[0]!["transportId"]!.GetValue<string>()));
        Assert.False(device.IsIdle);

        device.Sessions[0].Dispose();
    }

    [Fact]
    public async Task HandleAsync_LaunchUnknownAppWithAllowAnyAppFalse_RepliesLaunchErrorAndStaysIdle()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, device, _, handler) = CreateSut(channel, o => o.AllowAnyApp = false);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"LAUNCH","requestId":3,"appId":"UNKNOWN1"}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("LAUNCH_ERROR", payload["type"]!.GetValue<string>());
        Assert.Equal("NOT_FOUND", payload["reason"]!.GetValue<string>());
        Assert.True(device.IsIdle);
    }

    [Fact]
    public async Task HandleAsync_Stop_EndsSessionAndDeviceGoesIdleAgain()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, device, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"LAUNCH","requestId":4,"appId":"CC1AD845"}"""), CancellationToken.None);
        await channel.ReadAsync();
        var sessionId = device.Sessions[0].SessionId;

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, $$"""{"type":"STOP","requestId":5,"sessionId":"{{sessionId}}"}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("RECEIVER_STATUS", payload["type"]!.GetValue<string>());
        Assert.True(device.IsIdle);
    }

    [Fact]
    public async Task HandleAsync_SetVolume_UpdatesLevelAndMutedClampingToOne()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, device, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"SET_VOLUME","requestId":6,"volume":{"level":1.5,"muted":true}}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var volume = JsonNode.Parse(reply.PayloadUtf8)!.AsObject()["status"]!["volume"]!.AsObject();
        Assert.Equal(1, volume["level"]!.GetValue<double>());
        Assert.True(volume["muted"]!.GetValue<bool>());
        Assert.Equal(1, device.Volume.Level);
        Assert.True(device.Volume.Muted);
    }

    [Fact]
    public async Task HandleAsync_GetAppAvailability_RepliesWithResponseTypeNotType()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, _, _, handler) = CreateSut(channel, o => o.AllowAnyApp = false);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"GET_APP_AVAILABILITY","requestId":7,"appId":["CC1AD845","UNKNOWN"]}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Null(payload["type"]);
        Assert.Equal("GET_APP_AVAILABILITY", payload["responseType"]!.GetValue<string>());
        var availability = payload["availability"]!.AsObject();
        Assert.Equal("APP_AVAILABLE", availability["CC1AD845"]!.GetValue<string>());
        Assert.Equal("APP_UNAVAILABLE", availability["UNKNOWN"]!.GetValue<string>());
    }

    [Fact]
    public async Task HandleAsync_UnhandledReceiverMessageType_DoesNotThrow()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var (_, _, _, handler) = CreateSut(channel);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Receiver, """{"type":"BOGUS","requestId":8}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        var payload = JsonNode.Parse(reply.PayloadUtf8)!.AsObject();
        Assert.Equal("INVALID_REQUEST", payload["type"]!.GetValue<string>());
        Assert.Equal("INVALID_COMMAND", payload["reason"]!.GetValue<string>());
    }
}
