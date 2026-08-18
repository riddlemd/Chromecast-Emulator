using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.TestSupport;

/// A real TLS Cast channel over loopback: SslStream refuses to write before a
/// handshake, so faking the socket is not an option.
public sealed class LoopbackChannel : IAsyncDisposable
{
    private readonly TcpClient _senderTcp;
    private readonly SslStream _senderStream;

    private LoopbackChannel(TcpClient senderTcp, SslStream senderStream, CastConnection receiver)
    {
        _senderTcp = senderTcp;
        _senderStream = senderStream;
        Receiver = receiver;
    }

    /// The emulator's end, as handlers see it.
    public CastConnection Receiver { get; }

    public static async Task<LoopbackChannel> CreateAsync(CastFrameLog? frames = null)
    {
        var identity = SharedIdentity.Value;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var senderTcp = new TcpClient();
        var connect = senderTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var receiverTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var receiverStream = new SslStream(receiverTcp.GetStream(), leaveInnerStreamOpen: false);
        var senderStream = new SslStream(senderTcp.GetStream(), leaveInnerStreamOpen: false);

        await Task.WhenAll(
            receiverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }),
            senderStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "emulator",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }));

        var connection = new CastConnection(receiverTcp, receiverStream, identity.Certificate.RawData,
            frames ?? NullFrameLog(), NullLogger<CastConnection>.Instance);

        return new LoopbackChannel(senderTcp, senderStream, connection);
    }

    /// Throws rather than hanging when no frame arrives.
    public async Task<CastMessage> ReadAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await CastFrame.ReadAsync(_senderStream, cts.Token)
               ?? throw new EndOfStreamException("the receiver closed the channel without sending a frame");
    }

    /// Drains frames until one matches, so tests are not tripped by unrelated broadcasts.
    public async Task<CastMessage> ReadUntilAsync(Func<CastMessage, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadAsync(deadline - DateTime.UtcNow);
            if (predicate(message)) return message;
        }

        throw new TimeoutException("no matching frame arrived");
    }

    public Task SendAsync(string ns, string payload, string source = CastNamespaces.DefaultSenderId,
        string destination = CastNamespaces.PlatformReceiverId) =>
        _senderStream.WriteAsync(CastFrame.Encode(CastMessages.Text(ns, payload, source, destination))).AsTask();

    private static CastFrameLog NullFrameLog() =>
        new(NullLogger<CastFrameLog>.Instance, new EmulatorOptions());

    public async ValueTask DisposeAsync()
    {
        await Receiver.DisposeAsync();
        await _senderStream.DisposeAsync();
        _senderTcp.Dispose();
    }
}
