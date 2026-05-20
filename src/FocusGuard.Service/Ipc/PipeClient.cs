using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Service.Ipc;

/// <summary>
/// Minimal client for the FocusGuard named pipe. Lives in the Service assembly so the Tray
/// project picks it up via project reference; the framing format is private to this assembly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private bool _connected;

    public PipeClient(string pipeName, string serverName = ".")
    {
        _pipe = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await _pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        _connected = true;
    }

    public async Task<IpcResponseEnvelope> SendAsync<TPayload>(string command, TPayload payload, CancellationToken ct = default)
    {
        if (!_connected)
            throw new InvalidOperationException("PipeClient is not connected");

        var payloadElement = payload is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonSerializer.SerializeToElement(payload, IpcJson.Options);
        var envelope = new IpcRequestEnvelope(command, payloadElement);

        await PipeFraming.WriteAsync(_pipe, envelope, ct).ConfigureAwait(false);
        var response = await PipeFraming.ReadAsync<IpcResponseEnvelope>(_pipe, ct).ConfigureAwait(false);
        return response ?? new IpcResponseEnvelope(false, "no response", null);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
