using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Transport;

public readonly record struct VirtualConnection(string SenderId, string DestinationId);

/// One TLS socket from a sender. Carries many virtual connections, one per
/// (sender id, destination id) pair opened via the tp.connection namespace.
public sealed class CastConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<VirtualConnection> _virtual = [];
    private readonly Lock _gate = new();
    private readonly CastFrameLog _frames;
    private readonly ILogger<CastConnection> _logger;

    public CastConnection(TcpClient client, SslStream stream, byte[] serverCertificateDer,
        CastFrameLog frames, ILogger<CastConnection> logger)
    {
        _client = client;
        _stream = stream;
        _frames = frames;
        _logger = logger;
        ServerCertificateDer = serverCertificateDer;
        Peer = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public string Peer { get; }

    public byte[] ServerCertificateDer { get; }

    public Task<CastMessage?> ReadAsync(CancellationToken ct) => CastFrame.ReadAsync(_stream, ct);

    public void Open(VirtualConnection connection)
    {
        lock (_gate) _virtual.Add(connection);
    }

    public void Close(VirtualConnection connection)
    {
        lock (_gate) _virtual.Remove(connection);
    }

    public bool IsOpen(VirtualConnection connection)
    {
        lock (_gate) return _virtual.Contains(connection);
    }

    public IReadOnlyList<string> SendersFor(string destination)
    {
        lock (_gate)
            return _virtual.Where(v => v.DestinationId == destination).Select(v => v.SenderId).Distinct().ToArray();
    }

    public IReadOnlyList<VirtualConnection> Virtuals()
    {
        lock (_gate) return _virtual.ToArray();
    }

    public Task SendAsync(string source, string destination, string ns, JsonNode payload, CancellationToken ct) =>
        SendUtf8Async(source, destination, ns, payload.ToJsonString(), ct);

    /// Broadcast paths serialize the payload once and reuse the string per recipient.
    public Task SendUtf8Async(string source, string destination, string ns, string payloadUtf8, CancellationToken ct) =>
        SendAsync(new CastMessage
        {
            ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
            SourceId = source,
            DestinationId = destination,
            Namespace = ns,
            PayloadType = CastMessage.Types.PayloadType.String,
            PayloadUtf8 = payloadUtf8,
        }, ct);

    /// Replies answer on the namespace of the message they answer, with addressing swapped.
    public Task ReplyAsync(CastMessage to, JsonNode payload, CancellationToken ct) =>
        SendAsync(to.DestinationId, to.SourceId, to.Namespace, payload, ct);

    public Task ReplyBinaryAsync(CastMessage to, byte[] payload, CancellationToken ct) =>
        SendBinaryAsync(to.DestinationId, to.SourceId, to.Namespace, payload, ct);

    public Task SendBinaryAsync(string source, string destination, string ns, byte[] payload, CancellationToken ct) =>
        SendAsync(new CastMessage
        {
            ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
            SourceId = source,
            DestinationId = destination,
            Namespace = ns,
            PayloadType = CastMessage.Types.PayloadType.Binary,
            PayloadBinary = ByteString.CopyFrom(payload),
        }, ct);

    public async Task SendAsync(CastMessage message, CancellationToken ct)
    {
        var frame = CastFrame.Encode(message);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
            _frames.Outbound(Peer, message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogWarning("{Peer} write failed: {Reason}", Peer, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
        // The semaphore is deliberately not disposed: an in-flight SendAsync would hit
        // ObjectDisposedException outside its catch, and SemaphoreSlim holds no unmanaged
        // state unless AvailableWaitHandle was used.
    }
}
