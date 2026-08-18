using System.Buffers.Binary;
using ChromecastEmulator.Proto;
using Google.Protobuf;

namespace ChromecastEmulator.Transport;

/// CASTV2 framing: 4-byte big-endian payload length followed by a CastMessage.
public static class CastFrame
{
    /// Real devices cap frames at 64 KiB; allow slack so oversized senders fail loudly rather than silently.
    private const int MaxFrameBytes = 256 * 1024;

    public static async Task<CastMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct)) return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxFrameBytes)
            throw new InvalidDataException($"frame length {length} outside 1..{MaxFrameBytes}");

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct))
            throw new EndOfStreamException($"stream ended {length} bytes into a frame");

        return CastMessage.Parser.ParseFrom(payload);
    }

    public static byte[] Encode(CastMessage message)
    {
        var payload = message.ToByteArray();
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }
}
