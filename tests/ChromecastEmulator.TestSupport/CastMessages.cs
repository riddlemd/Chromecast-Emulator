using ChromecastEmulator.Proto;
using Google.Protobuf;

namespace ChromecastEmulator.TestSupport;

/// No socket involved, so unit tests can build frames without standing up a channel.
public static class CastMessages
{
    public static CastMessage Text(string ns, string payload,
        string source = CastNamespaces.DefaultSenderId,
        string destination = CastNamespaces.PlatformReceiverId) => new()
    {
        ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
        SourceId = source,
        DestinationId = destination,
        Namespace = ns,
        PayloadType = CastMessage.Types.PayloadType.String,
        PayloadUtf8 = payload,
    };

    public static CastMessage Binary(string ns, byte[] payload,
        string source = CastNamespaces.DefaultSenderId,
        string destination = CastNamespaces.PlatformReceiverId) => new()
    {
        ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
        SourceId = source,
        DestinationId = destination,
        Namespace = ns,
        PayloadType = CastMessage.Types.PayloadType.Binary,
        PayloadBinary = ByteString.CopyFrom(payload),
    };
}
