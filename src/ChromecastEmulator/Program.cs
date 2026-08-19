using ChromecastEmulator;
using ChromecastEmulator.Device;
using ChromecastEmulator.Discovery;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Render;
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

using var pipeline = options.Render
    ? new HlsPipeline(
        options,
        // Flat, not nested: the pipeline removes exactly this directory on dispose.
        Path.Combine(Path.GetTempPath(), $"chromecast-emulator-{Environment.ProcessId}"),
        loggerFactory.CreateLogger<HlsPipeline>())
    : null;
using var playerServer = pipeline is null
    ? null
    : new PlayerServer(options.RenderPort, pipeline.OutputDirectory, loggerFactory.CreateLogger<PlayerServer>());
var renderWindow = options.Render ? new RenderWindow(options, loggerFactory.CreateLogger<RenderWindow>()) : null;
using var renderer = pipeline is null || renderWindow is null
    ? null
    : new RenderController(pipeline, renderWindow, device, loggerFactory.CreateLogger<RenderController>());

if (renderer is not null)
{
    // Tearing down the app session ends playback, but nothing in the media namespace
    // reports it — the receiver namespace does.
    device.StatusChanged += () =>
    {
        if (!device.Sessions.Any(s => s.Media is not null)) renderer.Clear();
    };
    device.Volume.Changed += renderer.SetVolume;
}

router.Register(CastNamespaces.Connection, new ConnectionHandler(loggerFactory.CreateLogger<ConnectionHandler>()));
router.Register(CastNamespaces.Heartbeat, new HeartbeatHandler());
router.Register(CastNamespaces.DeviceAuth,
    new DeviceAuthHandler(options, identity, loggerFactory.CreateLogger<DeviceAuthHandler>()));
router.Register(CastNamespaces.Receiver,
    new ReceiverHandler(device, broadcaster, loggerFactory.CreateLogger<ReceiverHandler>()));
router.Register(CastNamespaces.Media,
    new MediaHandler(options, device, broadcaster, shutdown.Token, loggerFactory.CreateLogger<MediaHandler>(), renderer));
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

if (renderWindow is not null && playerServer is not null)
{
    playerServer.Start();

    // Closing the window quits, and `quit` or ctrl-c has to close the window — the main
    // thread is parked in the run loop below and would otherwise never come back.
    renderWindow.Closed += shutdown.Cancel;
    using var closeOnShutdown = shutdown.Token.Register(renderWindow.Close);

    // AppKit's run loop has to own the main thread, so the accept loop stays on the
    // thread pool and the window blocks here until it closes. Opening a window we have
    // already been asked to shut down would park the main thread for good.
    if (!shutdown.IsCancellationRequested) renderWindow.Run(playerServer.BaseUrl);
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
