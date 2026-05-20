using System.Runtime.Versioning;
using System.Text.Json;
using FocusGuard.Core;
using FocusGuard.Core.Ipc;
using FocusGuard.Service.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

// PipeClient lives in FocusGuard.Core.Ipc; PipeServer remains in FocusGuard.Service.Ipc.

namespace FocusGuard.Service.Tests;

/// <summary>
/// Drives the real <see cref="PipeServer"/>/<see cref="PipeClient"/> pair against a
/// <see cref="Worker"/> harness, verifying the JSON round-trip survives framing.
/// Skipped on non-Windows because named pipes via WCF/Win32 ACL helpers only run on Windows.
/// </summary>
public class PipeServerTests
{
    [SkippableFact]
    public async Task Round_trip_GetStatus_returns_blocked_with_default_minutes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Named pipes test requires Windows");
#pragma warning disable CA1416 // platform guarded by Skip
        await RunRoundTripAsync();
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunRoundTripAsync()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var server = new PipeServer(harness.Options.PipeName, harness.Worker, NullLogger<PipeServer>.Instance, restrictAcl: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = Task.Run(() => server.RunAsync(cts.Token));

        try
        {
            await using var client = new PipeClient(harness.Options.PipeName);
            await client.ConnectAsync(TimeSpan.FromSeconds(5), cts.Token);

            var response = await client.SendAsync(IpcCommands.GetStatus, new GetStatusRequest(), cts.Token);

            Assert.True(response.Success, response.Error);
            Assert.NotNull(response.Result);
            var status = JsonSerializer.Deserialize<StatusResponse>(response.Result!.Value.GetRawText(), IpcJson.Options);
            Assert.NotNull(status);
            Assert.Equal(FocusState.Blocked, status!.State);
            Assert.Equal(BudgetClock.DailyBudgetMinutes, status.MinutesRemaining);
            Assert.True(status.RequiresPasswordSetup);

            // Send an unknown command on the same connection — server should keep serving.
            var bogus = await client.SendAsync("Bogus", new { }, cts.Token);
            Assert.False(bogus.Success);
        }
        finally
        {
            cts.Cancel();
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
    }
}
