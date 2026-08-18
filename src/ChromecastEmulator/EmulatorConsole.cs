using System.Text.Json.Nodes;
using ChromecastEmulator.Device;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator;

public sealed class EmulatorConsole
{
    private readonly VirtualDevice _device;
    private readonly IConnectionRegistry _registry;
    private readonly StatusBroadcaster _broadcaster;
    private readonly CancellationTokenSource _shutdown;
    private readonly ILogger<EmulatorConsole> _logger;

    public EmulatorConsole(
        VirtualDevice device, IConnectionRegistry registry, StatusBroadcaster broadcaster, CancellationTokenSource shutdown,
        ILogger<EmulatorConsole> logger)
    {
        _device = device;
        _registry = registry;
        _broadcaster = broadcaster;
        _shutdown = shutdown;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var ct = _shutdown.Token;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Task.Run(Console.ReadLine, ct);
            }
            catch (OperationCanceledException)
            {
                // RunAsync is fire-and-forget; letting this escape would fault the task unobserved.
                return;
            }

            if (line is null) return;

            line = line.Trim();
            if (line.Length == 0) continue;

            var space = line.IndexOf(' ');
            var command = (space < 0 ? line : line[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();

            try
            {
                if (!await ExecuteAsync(command, rest, ct)) return;
            }
            catch (Exception ex)
            {
                // Mostly bad operand typed at the prompt; the message is the whole story.
                _logger.LogError("console: {Reason}", ex.Message);
            }
        }
    }

    private async Task<bool> ExecuteAsync(string command, string rest, CancellationToken ct)
    {
        switch (command)
        {
            case "help" or "?":
                Console.WriteLine(HelpText);
                break;

            case "status":
                PrintStatus();
                break;

            case "conns":
                PrintConnections();
                break;

            case "launch":
                await LaunchAsync(rest, ct);
                break;

            case "stop":
                if (_device.Stop(rest)) await _broadcaster.ReceiverStatusAsync(ct);
                else _logger.LogWarning("no session {SessionId}", rest);
                break;

            case "volume":
                // No NotifyStatusChanged: volume is not in the mDNS TXT records, so a
                // republish cycle would change nothing senders can discover.
                _device.Volume.Level = Math.Clamp(double.Parse(rest), 0, 1);
                await _broadcaster.ReceiverStatusAsync(ct);
                break;

            case "mute" or "unmute":
                _device.Volume.Muted = command == "mute";
                await _broadcaster.ReceiverStatusAsync(ct);
                break;

            case "play" or "pause" or "seek" or "stopmedia":
                await MediaAsync(command, rest, ct);
                break;

            case "send":
                await SendAsync(rest, ct);
                break;

            case "quit" or "exit":
                await _shutdown.CancelAsync();
                return false;

            default:
                _logger.LogWarning("unknown command '{Command}' - type 'help'", command);
                break;
        }

        return true;
    }

    private async Task LaunchAsync(string appId, CancellationToken ct)
    {
        if (appId.Length == 0)
        {
            _logger.LogWarning("usage: launch <appId>");
            return;
        }

        var session = _device.Launch(appId);
        if (session is null)
        {
            _logger.LogWarning("cannot launch {AppId}", appId);
            return;
        }

        _logger.LogInformation("launched {AppId} -> session {SessionId}, transport {TransportId}", appId, session.SessionId, session.TransportId);
        await _broadcaster.ReceiverStatusAsync(ct);
    }

    private async Task MediaAsync(string command, string rest, CancellationToken ct)
    {
        var session = _device.Sessions.FirstOrDefault(s => s.Media is not null);
        if (session?.Media is not { } media)
        {
            _logger.LogWarning("no media session loaded");
            return;
        }

        switch (command)
        {
            case "play": media.Play(); break;
            case "pause": media.Pause(); break;
            case "seek": media.Seek(double.Parse(rest), null); break;
            case "stopmedia": media.Stop(); break;
        }

        await _broadcaster.MediaStatusAsync(session, includeMedia: false, ct);
    }

    private async Task SendAsync(string rest, CancellationToken ct)
    {
        var space = rest.IndexOf(' ');
        if (space < 0)
        {
            _logger.LogWarning("usage: send <namespace> <json>");
            return;
        }

        var ns = rest[..space];
        var json = JsonNode.Parse(rest[(space + 1)..]) ?? new JsonObject();

        // Source must follow namespace ownership: receiver-namespace pushes come from
        // receiver-0, or senders connected only to the platform never see them.
        var source = ns == CastNamespaces.Receiver
            ? CastNamespaces.PlatformReceiverId
            : _device.Sessions.FirstOrDefault()?.TransportId ?? CastNamespaces.PlatformReceiverId;

        await _broadcaster.FanOutAsync(source, ns, json, ct);
    }

    private void PrintStatus()
    {
        Console.WriteLine($"device      {_device.FriendlyName} ({_device.DeviceId})");
        Console.WriteLine($"volume      level={_device.Volume.Level:F2} muted={_device.Volume.Muted}");
        Console.WriteLine($"connections {_registry.Connections.Count}");

        var sessions = _device.Sessions;
        if (sessions.Count == 0)
        {
            Console.WriteLine("sessions    (idle)");
            return;
        }

        foreach (var session in sessions)
        {
            Console.WriteLine($"session     {session.AppId} id={session.SessionId} transport={session.TransportId}");
            if (session.Media is { } media)
                Console.WriteLine($"  media     #{media.MediaSessionId} {media.PlayerState} {media.CurrentTime:F1}s / {media.Duration?.ToString("F1") ?? "?"}s");
        }
    }

    private void PrintConnections()
    {
        var connections = _registry.Connections;
        if (connections.Count == 0)
        {
            Console.WriteLine("no senders connected");
            return;
        }

        foreach (var connection in connections)
        {
            Console.WriteLine($"{connection.Peer}");
            foreach (var virtualConnection in connection.Virtuals())
                Console.WriteLine($"  {virtualConnection.SenderId} -> {virtualConnection.DestinationId}");
        }
    }

    private const string HelpText = """
        status                    device, volume and session state
        conns                     connected senders and their virtual connections
        launch <appId>            launch an app as if a sender asked
        stop <sessionId>          tear a session down
        volume <0-1>              set volume and broadcast RECEIVER_STATUS
        mute | unmute             toggle mute
        play | pause              drive the loaded media session
        seek <seconds>            seek the loaded media session
        stopmedia                 stop playback (IDLE/CANCELLED)
        send <ns> <json>          push a message on any namespace to connected senders
        quit                      shut down
        """;
}
