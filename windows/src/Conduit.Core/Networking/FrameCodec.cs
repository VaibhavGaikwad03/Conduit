using System.Buffers.Binary;

namespace Conduit.Core.Networking;

/// <summary>
/// Reads/writes length-prefixed frames on a stream: [4-byte big-endian length][payload].
/// This is the low-level framing described in PROTOCOL.md §2.
/// </summary>
public static class FrameCodec
{
    /// <summary>Maximum single frame size (16 MB) — guards against corrupt/hostile length prefixes.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameSize)
            throw new InvalidOperationException($"Frame too large: {payload.Length} bytes");

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one full frame. Returns null on clean end-of-stream.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (header is null) return null;

        uint len = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len > MaxFrameSize)
            throw new InvalidDataException($"Declared frame size {len} exceeds max {MaxFrameSize}");
        if (len == 0) return [];

        var payload = await ReadExactAsync(stream, (int)len, ct).ConfigureAwait(false);
        if (payload is null)
            throw new EndOfStreamException("Stream ended mid-frame");
        return payload;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
                return read == 0 ? null : throw new EndOfStreamException("Stream ended mid-frame");
            read += n;
        }
        return buffer;
    }
}
