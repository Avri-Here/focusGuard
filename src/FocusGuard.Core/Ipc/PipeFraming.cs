using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FocusGuard.Core.Ipc;

/// <summary>
/// Length-prefix framing for IPC: 4-byte big-endian payload length, then UTF-8 JSON.
/// Shared by server and client so a single representation lives in one place.
/// </summary>
public static class PipeFraming
{
    /// <summary>Hard cap on payload size to bound allocations from a misbehaving peer.</summary>
    public const int MaxPayloadBytes = 1 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options);
        if (json.Length > MaxPayloadBytes)
            throw new InvalidOperationException($"IPC payload {json.Length} exceeds limit {MaxPayloadBytes}");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return default;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidDataException($"IPC frame length {length} out of bounds");

        var buf = new byte[length];
        if (!await ReadExactAsync(stream, buf, ct).ConfigureAwait(false))
            throw new EndOfStreamException("IPC peer closed mid-frame");

        return JsonSerializer.Deserialize<T>(buf, IpcJson.Options);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException();
            offset += read;
        }
        return true;
    }

    public static string DescribeJson(ReadOnlySpan<byte> json) =>
        Encoding.UTF8.GetString(json);
}
