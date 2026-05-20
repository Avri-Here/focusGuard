using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FocusGuard.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Ipc;

// PipeFraming and PipeClient now live in FocusGuard.Core.Ipc so the Tray can reuse them
// without depending on the Service assembly.

/// <summary>
/// Hosts the named-pipe IPC endpoint. Accepts connections in a loop, framing JSON requests/
/// responses with a 4-byte big-endian length prefix. Each connection serves multiple
/// request/response pairs until the client disconnects.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer
{
    private readonly string _pipeName;
    private readonly ICommandHandler _handler;
    private readonly ILogger<PipeServer> _logger;
    private readonly bool _restrictAcl;

    public PipeServer(string pipeName, ICommandHandler handler, ILogger<PipeServer> logger, bool restrictAcl = true)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
        _restrictAcl = restrictAcl;
    }

    /// <summary>
    /// Run the accept loop until <paramref name="ct"/> is cancelled. Each accepted connection
    /// is served on its own task so a slow client cannot stall others.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("PipeServer starting on \\\\.\\pipe\\{Pipe}", _pipeName);
        var connections = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateServerStream();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create pipe server stream; retrying in 1s");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WaitForConnectionAsync failed");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            connections.Add(Task.Run(() => ServeConnectionAsync(pipe, ct), ct));
            connections.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
        _logger.LogInformation("PipeServer stopped");
    }

    private NamedPipeServerStream CreateServerStream()
    {
        const int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances;
        const PipeTransmissionMode mode = PipeTransmissionMode.Byte;
        const PipeOptions options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;

        if (!_restrictAcl)
        {
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, maxInstances, mode, options);
        }

        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(authUsers,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxInstances,
            mode,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    IpcRequestEnvelope? request;
                    try
                    {
                        request = await PipeFraming.ReadAsync<IpcRequestEnvelope>(pipe, ct).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }

                    if (request is null) break;

                    IpcResponseEnvelope response;
                    try
                    {
                        response = await _handler.HandleAsync(request, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handler threw for command {Command}", request.Command);
                        response = new IpcResponseEnvelope(false, $"server error: {ex.Message}", null);
                    }

                    await PipeFraming.WriteAsync(pipe, response, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Pipe IO ended");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed IPC frame; closing connection");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error serving pipe connection");
            }
        }
    }
}
