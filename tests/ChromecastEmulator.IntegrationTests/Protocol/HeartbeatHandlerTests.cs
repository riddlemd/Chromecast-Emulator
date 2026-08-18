using System.Text.Json.Nodes;
using ChromecastEmulator;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.TestSupport;

namespace ChromecastEmulator.IntegrationTests.Protocol;

public class HeartbeatHandlerTests
{
    [Fact]
    public async Task HandleAsync_Ping_RepliesPong()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new HeartbeatHandler();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Heartbeat, """{"type":"PING"}"""), CancellationToken.None);
        var reply = await channel.ReadAsync();

        Assert.Equal(CastNamespaces.Heartbeat, reply.Namespace);
        Assert.Equal(CastNamespaces.PlatformReceiverId, reply.SourceId);
        Assert.Equal(CastNamespaces.DefaultSenderId, reply.DestinationId);
        Assert.Equal("PONG", JsonNode.Parse(reply.PayloadUtf8)!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task HandleAsync_MalformedPayload_DoesNotThrowAndSendsNothing()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new HeartbeatHandler();

        await handler.HandleAsync(channel.Receiver,
            CastMessages.Text(CastNamespaces.Heartbeat, "not json"), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task PingAsync_WritesPingFrame()
    {
        await using var channel = await LoopbackChannel.CreateAsync();

        await HeartbeatHandler.PingAsync(channel.Receiver, CancellationToken.None);
        var reply = await channel.ReadAsync();

        Assert.Equal(CastNamespaces.Heartbeat, reply.Namespace);
        Assert.Equal(CastNamespaces.PlatformReceiverId, reply.SourceId);
        Assert.Equal(CastNamespaces.DefaultSenderId, reply.DestinationId);
        Assert.Equal("PING", JsonNode.Parse(reply.PayloadUtf8)!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task HandleAsync_PongFromSender_AcceptedWithoutReply()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new HeartbeatHandler();

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Heartbeat, """{"type":"PONG"}"""), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(TimeSpan.FromMilliseconds(300)));
    }
}
