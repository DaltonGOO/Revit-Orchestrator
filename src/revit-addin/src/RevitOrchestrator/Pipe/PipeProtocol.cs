using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Pipe;

/// <summary>
/// Length-prefixed JSON framing that matches the Python protocol implementation.
/// </summary>
public static class PipeProtocol
{
    public const int HeaderSize = 4;
    public const int MaxMessageSize = 16 * 1024 * 1024; // 16 MiB

    /// <summary>
    /// Encode a PipeMessage into a length-prefixed byte array.
    /// </summary>
    public static byte[] Encode(PipeMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);

        if (payload.Length > MaxMessageSize)
            throw new InvalidOperationException(
                $"Message size {payload.Length} exceeds maximum {MaxMessageSize}");

        var result = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, HeaderSize), (uint)payload.Length);
        payload.CopyTo(result, HeaderSize);
        return result;
    }

    /// <summary>
    /// Decode a 4-byte LE uint32 header into the payload length.
    /// </summary>
    public static int DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize)
            throw new ArgumentException($"Header must be {HeaderSize} bytes, got {header.Length}");

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (length > MaxMessageSize)
            throw new InvalidOperationException(
                $"Message size {length} exceeds maximum {MaxMessageSize}");

        return length;
    }

    /// <summary>
    /// Decode a UTF-8 JSON payload into a PipeMessage.
    /// </summary>
    public static PipeMessage DecodePayload(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<PipeMessage>(payload)
            ?? throw new InvalidOperationException("Failed to deserialize pipe message");
    }
}
