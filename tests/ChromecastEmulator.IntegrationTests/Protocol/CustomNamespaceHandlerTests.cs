using ChromecastEmulator;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Protocol;

public class CustomNamespaceHandlerTests
{
    private const string CustomNamespace = "urn:x-cast:com.example.custom";

    [Fact]
    public async Task HandleAsync_EchoCustomFalse_WritesNothingBack()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var options = new EmulatorOptions { EchoCustom = false };
        var handler = new CustomNamespaceHandler(options, NullLogger<CustomNamespaceHandler>.Instance);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(CustomNamespace, """{"hello":"world"}"""), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task HandleAsync_EchoCustomTrue_EchoesMessageWithSourceAndDestinationSwapped()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var options = new EmulatorOptions { EchoCustom = true };
        var handler = new CustomNamespaceHandler(options, NullLogger<CustomNamespaceHandler>.Instance);

        await handler.HandleAsync(channel.Receiver, CastMessages.Text(
            CustomNamespace, """{"hello":"world"}""", source: "sender-0", destination: "web-1"), CancellationToken.None);
        var reply = await channel.ReadAsync();

        Assert.Equal(CustomNamespace, reply.Namespace);
        Assert.Equal("web-1", reply.SourceId);
        Assert.Equal("sender-0", reply.DestinationId);
        Assert.Equal("""{"hello":"world"}""", reply.PayloadUtf8);
    }

    [Fact]
    public async Task HandleAsync_BinaryPayload_EchoesTheBytesUnchanged()
    {
        await using var channel = await LoopbackChannel.CreateAsync();
        var options = new EmulatorOptions { EchoCustom = true };
        var handler = new CustomNamespaceHandler(options, NullLogger<CustomNamespaceHandler>.Instance);

        await handler.HandleAsync(channel.Receiver,
            CastMessages.Binary(CustomNamespace, [0x01, 0x02, 0x03]), CancellationToken.None);
        var reply = await channel.ReadAsync();

        Assert.Equal(CastMessage.Types.PayloadType.Binary, reply.PayloadType);
        Assert.Equal([0x01, 0x02, 0x03], reply.PayloadBinary.ToByteArray());
    }
}
