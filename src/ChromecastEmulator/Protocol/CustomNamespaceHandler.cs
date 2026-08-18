using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

/// --echo mirrors the payload back so a sender's round-trip can be exercised without
/// writing a receiver app.
public sealed class CustomNamespaceHandler : ICastNamespaceHandler
{
    private readonly EmulatorOptions _options;
    private readonly ILogger<CustomNamespaceHandler> _logger;

    public CustomNamespaceHandler(EmulatorOptions options, ILogger<CustomNamespaceHandler> logger) => (_options, _logger) = (options, logger);

    public async Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        if (message.PayloadType == CastMessage.Types.PayloadType.Binary)
        {
            _logger.LogDebug("   custom binary message on {Namespace} ({ByteCount} bytes)", message.Namespace, message.PayloadBinary.Length);
            if (_options.EchoCustom)
                await connection.ReplyBinaryAsync(message, message.PayloadBinary.ToByteArray(), ct);
            return;
        }

        _logger.LogDebug("   custom message on {Namespace}: {Payload}", message.Namespace, message.PayloadUtf8);
        if (!_options.EchoCustom) return;

        var echo = JsonNode.Parse(message.PayloadUtf8) ?? new JsonObject();
        await connection.ReplyAsync(message, echo, ct);
    }
}
