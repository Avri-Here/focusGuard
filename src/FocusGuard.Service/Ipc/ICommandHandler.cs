using FocusGuard.Core.Ipc;

namespace FocusGuard.Service.Ipc;

/// <summary>
/// Receives parsed IPC envelopes from <see cref="PipeServer"/> and produces a response.
/// The Worker provides the implementation; the pipe server only owns transport.
/// </summary>
public interface ICommandHandler
{
    Task<IpcResponseEnvelope> HandleAsync(IpcRequestEnvelope request, CancellationToken ct);
}
