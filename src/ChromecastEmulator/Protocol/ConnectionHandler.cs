using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

public sealed class ConnectionHandler : ICastNamespaceHandler
{
    private readonly ILogger<ConnectionHandler> _logger;

    public ConnectionHandler(ILogger<ConnectionHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        var type = JsonNode.Parse(message.PayloadUtf8)?["type"]?.GetValue<string>();
        var virtualConnection = new VirtualConnection(message.SourceId, message.DestinationId);

        switch (type)
        {
            case "CONNECT":
                connection.Open(virtualConnection);
                _logger.LogDebug("   virtual connection opened {SenderId} -> {DestinationId}", virtualConnection.SenderId, virtualConnection.DestinationId);
                break;

            case "CLOSE":
                connection.Close(virtualConnection);
                _logger.LogDebug("   virtual connection closed {SenderId} -> {DestinationId}", virtualConnection.SenderId, virtualConnection.DestinationId);
                break;

            default:
                _logger.LogWarning("unhandled connection message '{MessageType}'", type);
                break;
        }

        return Task.CompletedTask;
    }
}
