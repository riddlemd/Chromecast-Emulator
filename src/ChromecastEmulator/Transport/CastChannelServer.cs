using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ChromecastEmulator.Protocol;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Transport;

public sealed class CastChannelServer : IConnectionRegistry
{
    private readonly EmulatorOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly MessageRouter _router;
    private readonly CastFrameLog _frames;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CastChannelServer> _logger;
    private readonly List<CastConnection> _connections = [];
    private readonly Lock _gate = new();

    public CastChannelServer(EmulatorOptions options, DeviceIdentity identity, MessageRouter router,
        CastFrameLog frames, ILoggerFactory loggerFactory)
    {
        _options = options;
        _identity = identity;
        _router = router;
        _frames = frames;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CastChannelServer>();
    }

    public IReadOnlyList<CastConnection> Connections
    {
        get { lock (_gate) return _connections.ToArray(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Start();
        _logger.LogInformation("cast channel listening on {BindAddress}:{Port}", _options.BindAddress, _options.Port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                // No scheduling token: if ct cancels before the delegate runs, ServeAsync's
                // dispose path would be skipped and the accepted client leaked.
                _ = Task.Run(() => ServeAsync(client, ct));
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;

        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        CastConnection? connection = null;

        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);

            connection = new CastConnection(client, stream, _identity.Certificate.RawData, _frames,
                _loggerFactory.CreateLogger<CastConnection>());
            lock (_gate) _connections.Add(connection);
            _logger.LogInformation("sender connected: {Peer}", connection.Peer);

            using var pinger = StartHeartbeat(connection, ct);

            while (!ct.IsCancellationRequested)
            {
                var message = await connection.ReadAsync(ct);
                if (message is null) break;

                _frames.Inbound(connection.Peer, message);
                await _router.DispatchAsync(connection, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (AuthenticationException ex)
        {
            // Message only: senders walk away routinely and the stack is always this read loop.
            _logger.LogWarning("TLS handshake failed from {Peer}: {Reason}", client.Client.RemoteEndPoint, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            _logger.LogWarning("{Peer} dropped: {Reason}", connection?.Peer ?? "sender", ex.Message);
        }
        finally
        {
            if (connection is not null)
            {
                lock (_gate) _connections.Remove(connection);
                _logger.LogInformation("sender disconnected: {Peer}", connection.Peer);
                await connection.DisposeAsync();
            }
            else
            {
                await stream.DisposeAsync();
                client.Dispose();
            }
        }
    }

    private CancellationTokenSource StartHeartbeat(CastConnection connection, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.PingInterval <= TimeSpan.Zero) return cts;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.PingInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                    await HeartbeatHandler.PingAsync(connection, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // connection closed
            }
        }, cts.Token);

        return cts;
    }
}
