using System.IO.Pipes;
using System.Text.Json;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Pipe;

/// <summary>
/// Named pipe client that connects to the Python MCP server.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PipeClient(string pipeName = "RevitOrchestrator")
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>
    /// Connect to the named pipe server.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(ct);
    }

    /// <summary>
    /// Send a message over the pipe.
    /// </summary>
    public async Task SendAsync(PipeMessage message, CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        var data = PipeProtocol.Encode(message);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(data, ct);
            await _pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Read a single framed message from the pipe.
    /// </summary>
    public async Task<PipeMessage> ReadAsync(CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected)
            throw new InvalidOperationException("Pipe is not connected");

        // Read header
        var header = new byte[PipeProtocol.HeaderSize];
        await ReadExactlyAsync(_pipe, header, ct);
        var length = PipeProtocol.DecodeHeader(header);

        // Read payload
        var payload = new byte[length];
        await ReadExactlyAsync(_pipe, payload, ct);
        return PipeProtocol.DecodePayload(payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Pipe connection closed");
            offset += read;
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _writeLock.Dispose();
    }
}
