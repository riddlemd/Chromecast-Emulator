using System.Text.Json.Nodes;

namespace ChromecastEmulator.Device;

public sealed class VirtualDevice
{
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CC1AD845"] = "Default Media Receiver",
        ["E8C28D3C"] = "Backdrop",
        ["233637DE"] = "YouTube",
        ["B3419EF5"] = "Google Cast Sample",
        ["9AC194DC"] = "Plex",
        ["A9BCCB7C"] = "Media Receiver",
    };

    private readonly EmulatorOptions _options;
    private readonly Dictionary<string, AppSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _transportCounter;

    public VirtualDevice(EmulatorOptions options, string deviceId)
    {
        _options = options;
        DeviceId = deviceId;
    }

    public string DeviceId { get; }
    public string FriendlyName => _options.FriendlyName;
    public string ModelName => _options.ModelName;
    public Volume Volume { get; } = new();

    /// Raised when the app/volume state changes so the mDNS TXT records can be refreshed.
    public event Action? StatusChanged;

    public IReadOnlyList<AppSession> Sessions
    {
        get { lock (_gate) return _sessions.Values.ToArray(); }
    }

    public bool IsIdle
    {
        get { lock (_gate) return _sessions.Count == 0; }
    }

    public string StatusText
    {
        get
        {
            lock (_gate) return _sessions.Count == 0 ? "" : $"Casting: {_sessions.Values.First().DisplayName}";
        }
    }

    public bool CanLaunch(string appId) => _options.AllowAnyApp || KnownApps.ContainsKey(appId);

    public AppSession? Launch(string appId)
    {
        if (!CanLaunch(appId)) return null;

        AppSession session;
        lock (_gate)
        {
            var existing = _sessions.Values.FirstOrDefault(s => string.Equals(s.AppId, appId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            // A real device runs one foreground app; launching a second replaces the first.
            foreach (var stale in _sessions.Values) stale.Dispose();
            _sessions.Clear();

            var namespaces = new List<string> { CastNamespaces.Media, CastNamespaces.Broadcast };
            namespaces.AddRange(_options.ExtraNamespaces);
            namespaces.Add($"urn:x-cast:{appId.ToLowerInvariant()}");

            session = new AppSession
            {
                AppId = appId,
                DisplayName = KnownApps.TryGetValue(appId, out var name) ? name : appId,
                SessionId = Guid.NewGuid().ToString(),
                TransportId = $"web-{Interlocked.Increment(ref _transportCounter)}",
                Namespaces = namespaces.Distinct(StringComparer.Ordinal).ToArray(),
            };

            _sessions[session.SessionId] = session;
        }

        StatusChanged?.Invoke();
        return session;
    }

    public bool Stop(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out var session)) return false;
            session.Dispose();
        }

        StatusChanged?.Invoke();
        return true;
    }

    public AppSession? BySessionId(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public AppSession? ByTransportId(string transportId)
    {
        lock (_gate) return _sessions.Values.FirstOrDefault(s => s.TransportId == transportId);
    }

    public void NotifyStatusChanged() => StatusChanged?.Invoke();

    public JsonObject BuildReceiverStatus()
    {
        var applications = new JsonArray();

        lock (_gate)
        {
            if (_sessions.Count == 0)
            {
                // Idle devices still report the backdrop app; senders use isIdleScreen
                // to tell "nothing casting" apart from "app list unavailable".
                applications.Add(new AppSession
                {
                    AppId = "E8C28D3C",
                    DisplayName = "Backdrop",
                    SessionId = Guid.Empty.ToString(),
                    TransportId = "web-0",
                    Namespaces = [CastNamespaces.Broadcast],
                    StatusText = "",
                    IsIdleScreen = true,
                }.ToJson());
            }
            else
            {
                foreach (var session in _sessions.Values) applications.Add(session.ToJson());
            }
        }

        return new JsonObject
        {
            ["applications"] = applications,
            ["userEq"] = new JsonObject(),
            ["volume"] = Volume.ToJson(),
        };
    }
}
