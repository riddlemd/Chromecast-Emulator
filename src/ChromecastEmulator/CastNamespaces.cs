namespace ChromecastEmulator;

/// Named CastNamespaces rather than Namespaces so it is never shadowed by the
/// AppSession.Namespaces property inside object initialisers.
public static class CastNamespaces
{
    public const string Connection = "urn:x-cast:com.google.cast.tp.connection";
    public const string Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string DeviceAuth = "urn:x-cast:com.google.cast.tp.deviceauth";
    public const string Receiver = "urn:x-cast:com.google.cast.receiver";
    public const string Media = "urn:x-cast:com.google.cast.media";
    public const string Broadcast = "urn:x-cast:com.google.cast.broadcast";

    public const string PlatformReceiverId = "receiver-0";
    public const string DefaultSenderId = "sender-0";

    private const string GooglePrefix = "urn:x-cast:com.google.cast.";
    private const string CastPrefix = "urn:x-cast:";

    public static bool IsHeartbeat(string ns) => ns == Heartbeat;

    public static string Shorten(string ns) =>
        ns.StartsWith(GooglePrefix, StringComparison.Ordinal) ? ns[GooglePrefix.Length..]
        : ns.StartsWith(CastPrefix, StringComparison.Ordinal) ? ns[CastPrefix.Length..]
        : ns;
}
