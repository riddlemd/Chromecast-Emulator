using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Google.Protobuf;

namespace ChromecastEmulator.UnitTests.Transport;

public class CastFrameTests
{
    private static CastMessage StringMessage(string ns = "urn:x-cast:com.google.cast.receiver") => new()
    {
        SourceId = "sender-0",
        DestinationId = "receiver-0",
        Namespace = ns,
        PayloadType = CastMessage.Types.PayloadType.String,
        PayloadUtf8 = """{"type":"PING"}""",
    };

    private static CastMessage BinaryMessage() => new()
    {
        SourceId = "sender-0",
        DestinationId = "receiver-0",
        Namespace = "urn:x-cast:com.google.cast.tp.deviceauth",
        PayloadType = CastMessage.Types.PayloadType.Binary,
        PayloadBinary = ByteString.CopyFrom([0x01, 0x02, 0x03, 0xFF]),
    };

    [Fact]
    public void Encode_WritesFourByteBigEndianLengthHeaderFollowedByMessage()
    {
        var message = StringMessage();
        var payload = message.ToByteArray();

        var frame = CastFrame.Encode(message);

        Assert.Equal(4 + payload.Length, frame.Length);
        var expectedHeader = new byte[]
        {
            (byte)(payload.Length >> 24),
            (byte)(payload.Length >> 16),
            (byte)(payload.Length >> 8),
            (byte)payload.Length,
        };
        Assert.Equal(expectedHeader, frame[..4]);
        Assert.Equal(payload, frame[4..]);
    }

    [Fact]
    public async Task EncodeThenReadAsync_StringPayload_RoundTripsAllFields()
    {
        var original = StringMessage();
        using var stream = new MemoryStream(CastFrame.Encode(original));

        var result = await CastFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(original.SourceId, result.SourceId);
        Assert.Equal(original.DestinationId, result.DestinationId);
        Assert.Equal(original.Namespace, result.Namespace);
        Assert.Equal(original.PayloadType, result.PayloadType);
        Assert.Equal(original.PayloadUtf8, result.PayloadUtf8);
    }

    [Fact]
    public async Task EncodeThenReadAsync_BinaryPayload_RoundTripsAllFields()
    {
        var original = BinaryMessage();
        using var stream = new MemoryStream(CastFrame.Encode(original));

        var result = await CastFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(original.SourceId, result.SourceId);
        Assert.Equal(original.DestinationId, result.DestinationId);
        Assert.Equal(original.Namespace, result.Namespace);
        Assert.Equal(original.PayloadType, result.PayloadType);
        Assert.Equal(original.PayloadBinary, result.PayloadBinary);
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream([]);

        var result = await CastFrame.ReadAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadAsync_DeclaredLengthZero_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream([0x00, 0x00, 0x00, 0x00]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CastFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_DeclaredLengthAboveCap_ThrowsInvalidDataException()
    {
        // 256 KiB cap; one byte over it must be rejected before any payload read is attempted.
        var overCap = (256 * 1024) + 1;
        var header = new byte[]
        {
            (byte)(overCap >> 24),
            (byte)(overCap >> 16),
            (byte)(overCap >> 8),
            (byte)overCap,
        };
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => CastFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_StreamEndsPartWayThroughDeclaredFrame_ThrowsEndOfStreamException()
    {
        var full = CastFrame.Encode(StringMessage());
        var truncated = full[..^3];
        using var stream = new MemoryStream(truncated);

        await Assert.ThrowsAsync<EndOfStreamException>(() => CastFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_FrameDeliveredInSmallChunks_ReassemblesCorrectly()
    {
        var original = StringMessage();
        var bytes = CastFrame.Encode(original);
        using var stream = new ChunkedStream(bytes, maxBytesPerRead: 3);

        var result = await CastFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(original.SourceId, result.SourceId);
        Assert.Equal(original.Namespace, result.Namespace);
        Assert.Equal(original.PayloadUtf8, result.PayloadUtf8);
    }

    [Fact]
    public async Task ReadAsync_TwoFramesBackToBack_ReadBackInOrder()
    {
        var first = StringMessage("urn:x-cast:com.google.cast.receiver");
        var second = StringMessage("urn:x-cast:com.google.cast.media");
        using var stream = new MemoryStream([.. CastFrame.Encode(first), .. CastFrame.Encode(second)]);

        var readFirst = await CastFrame.ReadAsync(stream, CancellationToken.None);
        var readSecond = await CastFrame.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(first.Namespace, readFirst!.Namespace);
        Assert.Equal(second.Namespace, readSecond!.Namespace);
    }

    /// Short reads prove CastFrame's ReadExactAsync loop reassembles a split frame.
    private sealed class ChunkedStream(byte[] data, int maxBytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = data.Length - _position;
            if (remaining == 0) return ValueTask.FromResult(0);

            var count = Math.Min(Math.Min(maxBytesPerRead, buffer.Length), remaining);
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
