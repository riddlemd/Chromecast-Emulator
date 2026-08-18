using ChromecastEmulator;
using ChromecastEmulator.Device;
using ChromecastEmulator.Discovery;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

var (options, error) = EmulatorOptions.Parse(args);

if (error is not null)
{
    Console.Error.WriteLine($"chromecast-emulator: {error}");
    Console.Error.WriteLine("try --help");
    return 2;
}

if (options is null)
{
    Console.WriteLine(EmulatorOptions.HelpText);
    return 0;
}

using var loggerFactory = CastLogging.Create(options);
var log = loggerFactory.CreateLogger("chromecast-emulator");
var frames = new CastFrameLog(loggerFactory.CreateLogger<CastFrameLog>(), options);

var identity = DeviceIdentity.Load(options);
var device = new VirtualDevice(options, identity.DeviceId);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var router = new MessageRouter(loggerFactory.CreateLogger<MessageRouter>());
var server = new CastChannelServer(options, identity, router, frames, loggerFactory);
var broadcaster = new StatusBroadcaster(server, device);

router.Register(CastNamespaces.Connection, new ConnectionHandler(loggerFactory.CreateLogger<ConnectionHandler>()));
router.Register(CastNamespaces.Heartbeat, new HeartbeatHandler());
router.Register(CastNamespaces.DeviceAuth,
    new DeviceAuthHandler(options, identity, loggerFactory.CreateLogger<DeviceAuthHandler>()));
router.Register(CastNamespaces.Receiver,
    new ReceiverHandler(device, broadcaster, loggerFactory.CreateLogger<ReceiverHandler>()));
router.Register(CastNamespaces.Media,
    new MediaHandler(options, device, broadcaster, shutdown.Token, loggerFactory.CreateLogger<MediaHandler>()));
router.RegisterFallback(new CustomNamespaceHandler(options, loggerFactory.CreateLogger<CustomNamespaceHandler>()));

log.LogInformation("device id {DeviceId}  \"{FriendlyName}\"  auth={AuthMode}",
    identity.DeviceId, options.FriendlyName, options.AuthMode.ToString().ToLowerInvariant());

using var advertiser = options.Advertise
    ? new MdnsAdvertiser(options, device, identity.BaseStationId, loggerFactory.CreateLogger<MdnsAdvertiser>())
    : null;
try
{
    advertiser?.Start();
}
catch (Exception ex)
{
    log.LogError(ex, "mDNS advertisement failed; senders must connect by IP");
}

var serverTask = server.RunAsync(shutdown.Token);

if (!options.NoConsole && !Console.IsInputRedirected)
{
    log.LogInformation("type 'help' for console commands");
    _ = new EmulatorConsole(device, server, broadcaster, shutdown, loggerFactory.CreateLogger<EmulatorConsole>())
        .RunAsync();
}

try
{
    await serverTask;
}
catch (OperationCanceledException)
{
    // ctrl-c
}

log.LogInformation("stopped");
return 0;
