using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator;

public sealed class CastFrameLog(ILogger<CastFrameLog> logger, EmulatorOptions options)
{
    internal const string DirectionProperty = "Direction";

    private const int PreviewLimit = 180;

    private const string Template =
        "{Arrow} {Peer,-21} {SourceId,-12} -> {DestinationId,-12} {ShortNamespace,-10} {MessageType,-22} {PayloadPreview}";

    public void Inbound(string peer, CastMessage message) => Frame("in", "<-", peer, message);

    public void Outbound(string peer, CastMessage message) => Frame("out", "->", peer, message);

    private void Frame(string direction, string arrow, string peer, CastMessage message)
    {
        // Heartbeats sit below the default console level, so -v is what surfaces them.
        var level = CastNamespaces.IsHeartbeat(message.Namespace) ? LogLevel.Trace : LogLevel.Information;
        if (!logger.IsEnabled(level)) return;

        var binary = message.PayloadType == CastMessage.Types.PayloadType.Binary;

        var payload = binary
            ? Convert.ToBase64String(message.PayloadBinary.ToByteArray())
            : message.PayloadUtf8;

        var preview = binary ? $"<{message.PayloadBinary.Length} bytes>" : message.PayloadUtf8;
        if (!options.Verbose && preview.Length > PreviewLimit) preview = preview[..PreviewLimit] + "...";

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   [DirectionProperty] = direction,
                   ["Namespace"] = message.Namespace,
                   ["Payload"] = payload,
               }))
        {
            logger.Log(level, Template, arrow, peer, message.SourceId, message.DestinationId,
                CastNamespaces.Shorten(message.Namespace), MessageType(message), preview);
        }
    }

    private static string MessageType(CastMessage message)
    {
        if (message.PayloadType == CastMessage.Types.PayloadType.Binary) return "BINARY";

        try
        {
            // GET_APP_AVAILABILITY answers carry responseType instead of type.
            var payload = JsonNode.Parse(message.PayloadUtf8);
            return payload?["type"]?.GetValue<string>()
                   ?? payload?["responseType"]?.GetValue<string>()
                   ?? "-";
        }
        catch (JsonException)
        {
            return "-";
        }
    }
}
