using ChromecastEmulator.Device;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Discovery;

/// Senders filter on the TXT records -- md/ve/ca in particular -- before they will
/// even show the device, so the set is not decorative.
public sealed class MdnsAdvertiser : IDisposable
{
    private const string ServiceName = "_googlecast._tcp";

    private readonly EmulatorOptions _options;
    private readonly VirtualDevice _device;
    private readonly string _baseStationId;
    private readonly Lock _gate = new();
    private readonly ILogger<MdnsAdvertiser> _logger;

    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    public MdnsAdvertiser(EmulatorOptions options, VirtualDevice device, string baseStationId, ILogger<MdnsAdvertiser> logger)
    {
        _options = options;
        _device = device;
        _baseStationId = baseStationId;
        _logger = logger;
    }

    public void Start()
    {
        _mdns = new MulticastService();
        _discovery = new ServiceDiscovery(_mdns);
        _profile = BuildProfile();
        _discovery.Advertise(_profile);
        _mdns.Start();

        _device.StatusChanged += Refresh;
        _logger.LogInformation("advertising \"{FriendlyName}\" as {ServiceName} on port {Port}", _device.FriendlyName, ServiceName, _options.Port);

        if (_options.Verbose)
            foreach (var resource in _profile.Resources)
                _logger.LogDebug("   mdns {Resource}", resource);
    }

    /// TXT st/rs change when an app launches; re-publish so senders polling
    /// discovery see the running-app state without connecting.
    private void Refresh()
    {
        lock (_gate)
        {
            if (_discovery is null || _profile is null) return;

            try
            {
                _discovery.Unadvertise(_profile);
                _profile = BuildProfile();
                _discovery.Advertise(_profile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS refresh failed");
            }
        }
    }

    private ServiceProfile BuildProfile()
    {
        var profile = new ServiceProfile(_device.FriendlyName, ServiceName, (ushort)_options.Port);

        // Makaretu derives the host name from the instance name, so a name with spaces
        // yields an SRV target like "Test\032Cast.googlecast.local" that strict sender-side
        // resolvers choke on. Real devices publish "<deviceid>.local".
        var hostName = new DomainName($"{_device.DeviceId}.local");
        profile.HostName = hostName;

        foreach (var srv in profile.Resources.OfType<SRVRecord>()) srv.Target = hostName;
        foreach (var address in profile.Resources.OfType<AddressRecord>()) address.Name = hostName;

        var text = profile.Resources.OfType<TXTRecord>().Single();
        text.Strings.Clear();

        foreach (var (key, value) in Records())
            text.Strings.Add($"{key}={value}");

        return profile;
    }

    private IEnumerable<(string Key, string Value)> Records()
    {
        yield return ("id", _device.DeviceId);
        yield return ("cd", _device.DeviceId.ToUpperInvariant());
        yield return ("rm", "");
        yield return ("ve", "05");
        yield return ("md", _device.ModelName);
        yield return ("ic", "/setup/icon.png");
        yield return ("fn", _device.FriendlyName);
        yield return ("ca", "4101");
        yield return ("st", _device.IsIdle ? "0" : "1");
        yield return ("bs", _baseStationId);
        yield return ("nf", "1");
        yield return ("rs", _device.StatusText);
    }

    public void Dispose()
    {
        _device.StatusChanged -= Refresh;

        // Unsubscribing does not stop a Refresh already in flight on a timer or console
        // thread; the gate does, and nulling the fields makes any later Refresh a no-op.
        lock (_gate)
        {
            try
            {
                if (_profile is not null) _discovery?.Unadvertise(_profile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS unadvertise failed");
            }

            _discovery?.Dispose();
            _mdns?.Dispose();
            _discovery = null;
            _mdns = null;
            _profile = null;
        }
    }
}
