using ChromecastEmulator;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Transport;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Protocol;

public class ConnectionHandlerTests
{
    [Fact]
    public async Task HandleAsync_Connect_OpensVirtualConnection()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new ConnectionHandler(NullLogger<ConnectionHandler>.Instance);
        var expected = new VirtualConnection(CastNamespaces.DefaultSenderId, CastNamespaces.PlatformReceiverId);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}"""), CancellationToken.None);

        Assert.True(channel.Receiver.IsOpen(expected));
        Assert.Contains(expected, channel.Receiver.Virtuals());
    }

    [Fact]
    public async Task HandleAsync_Close_RemovesVirtualConnection()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new ConnectionHandler(NullLogger<ConnectionHandler>.Instance);
        var virtualConnection = new VirtualConnection(CastNamespaces.DefaultSenderId, CastNamespaces.PlatformReceiverId);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}"""), CancellationToken.None);
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CLOSE"}"""), CancellationToken.None);

        Assert.False(channel.Receiver.IsOpen(virtualConnection));
    }

    [Fact]
    public async Task HandleAsync_UnknownMessageType_LeavesVirtualConnectionSetUnchanged()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new ConnectionHandler(NullLogger<ConnectionHandler>.Instance);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}"""), CancellationToken.None);
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"BOGUS"}"""), CancellationToken.None);

        Assert.Single(channel.Receiver.Virtuals());
    }

    [Fact]
    public async Task HandleAsync_ConnectToAppTransportId_TrackedSeparatelyFromReceiver()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new ConnectionHandler(NullLogger<ConnectionHandler>.Instance);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}""", destination: CastNamespaces.PlatformReceiverId), CancellationToken.None);
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}""", destination: "web-1"), CancellationToken.None);

        Assert.True(channel.Receiver.IsOpen(new VirtualConnection(CastNamespaces.DefaultSenderId, CastNamespaces.PlatformReceiverId)));
        Assert.True(channel.Receiver.IsOpen(new VirtualConnection(CastNamespaces.DefaultSenderId, "web-1")));
        Assert.Equal(2, channel.Receiver.Virtuals().Count);
    }

    [Fact]
    public async Task SendersFor_MultipleSendersConnectedToSameDestination_ReturnsDistinctSenders()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var handler = new ConnectionHandler(NullLogger<ConnectionHandler>.Instance);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}""", source: "sender-a", destination: "web-1"), CancellationToken.None);
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}""", source: "sender-b", destination: "web-1"), CancellationToken.None);
        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CastNamespaces.Connection, """{"type":"CONNECT"}""", source: "sender-a", destination: "web-1"), CancellationToken.None);

        var senders = channel.Receiver.SendersFor("web-1");

        Assert.Equal(2, senders.Count);
        Assert.Contains("sender-a", senders);
        Assert.Contains("sender-b", senders);
    }
}
