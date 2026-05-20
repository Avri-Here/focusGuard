using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Tray.Services;

/// <summary>
/// Thin wrapper around <see cref="PipeClient"/>. Opens a fresh pipe per call so a hung
/// connection can never wedge the tray. All methods are best-effort: a transport error
/// is reported as a structured response with <c>Success=false</c>, never as a thrown exception
/// out of normal use (cancellation still propagates).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayClient
{
    public const string DefaultPipeName = "focusguard.cmd";

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public TrayClient(string pipeName = DefaultPipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(750);
    }

    public async Task<IpcResponseEnvelope> SendAsync<TPayload>(string command, TPayload payload, CancellationToken ct = default)
    {
        try
        {
            await using var client = new PipeClient(_pipeName);
            await client.ConnectAsync(_connectTimeout, ct).ConfigureAwait(false);
            return await client.SendAsync(command, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            return new IpcResponseEnvelope(false, "service unavailable: " + ex.Message, null);
        }
        catch (IOException ex)
        {
            return new IpcResponseEnvelope(false, "service unavailable: " + ex.Message, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new IpcResponseEnvelope(false, "service unavailable: " + ex.Message, null);
        }
        catch (Exception ex)
        {
            return new IpcResponseEnvelope(false, "service error: " + ex.Message, null);
        }
    }

    public async Task<StatusResponse?> TryGetStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(IpcCommands.GetStatus, new GetStatusRequest(), ct).ConfigureAwait(false);
        if (!resp.Success || resp.Result is null) return null;
        return JsonSerializer.Deserialize<StatusResponse>(resp.Result.Value.GetRawText(), IpcJson.Options);
    }
}
