using FocusGuard.Core;
using FocusGuard.Core.Audit;
using FocusGuard.Core.Ipc;
using FocusGuard.Core.Security;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Step 10 of the build plan: tamper-resistance integration tests at the Worker level.
///
/// The pure-Core layers already cover their own slices:
///   - <c>BudgetClockTests.ClockTamper_*</c>: tamper detection + threshold (Core).
///   - <c>WorkerWatchdogTests</c>: watchdog respawn loop (Service, step 9).
///
/// What's left is verifying the Worker reacts the way the plan promises when those
/// signals arrive: emit an audit entry, end any active session, refuse any further
/// budget consumption until the wall clock catches up. Service-stop ACL hardening
/// and config-dir ACL are install-time concerns covered by VERIFICATION.md.
/// </summary>
public class TamperTests
{
    private static readonly PasswordHasher CheapHasher = new(
        new Argon2idParams(MemoryKb: 8, Iterations: 1, Parallelism: 1, HashLengthBytes: 16, SaltLengthBytes: 8));

    [Fact]
    public async Task ClockBackwardJump_during_browsing_ends_session_and_emits_tamper_audit()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = CheapHasher.Hash("pwd") });
        await harness.StartAsync();

        // Enter Browsing.
        var start = await harness.Worker.HandleAsync(
            new IpcRequestEnvelope(IpcCommands.StartBudget, default),
            CancellationToken.None);
        Assert.True(start.Success);

        // Wall jumps backward by 30 minutes; monotonic does not.
        harness.Clock.AdvanceWallOnly(TimeSpan.FromMinutes(-30));
        await harness.Worker.TickForTestsAsync();

        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.Equal(NetworkPosture.Closed, harness.Firewall.LastPosture);

        Assert.Contains(harness.Audit.Entries, e => e.Category == AuditCategory.Tamper);
        Assert.Contains(harness.Audit.Entries, e =>
            e.Category == AuditCategory.StateTransition
            && e.Message.Contains("Browsing", StringComparison.Ordinal)
            && e.Message.Contains("Blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClockBackwardJump_while_blocked_emits_tamper_audit_but_does_not_transition()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = CheapHasher.Hash("pwd") });
        await harness.StartAsync();

        harness.Clock.AdvanceWallOnly(TimeSpan.FromMinutes(-30));
        await harness.Worker.TickForTestsAsync();

        Assert.Contains(harness.Audit.Entries, e => e.Category == AuditCategory.Tamper);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.DoesNotContain(harness.Audit.Entries, e => e.Category == AuditCategory.StateTransition);
    }

    [Fact]
    public async Task StateTransition_audit_logs_admin_pause_and_endpause()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = CheapHasher.Hash("hunter2") });
        await harness.StartAsync();

        var pauseResp = await harness.Worker.HandleAsync(
            new IpcRequestEnvelope(
                IpcCommands.AdminPause,
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new AdminPauseRequest("hunter2", 15), IpcJson.Options)),
            CancellationToken.None);
        Assert.True(pauseResp.Success);

        Assert.Contains(harness.Audit.Entries, e =>
            e.Category == AuditCategory.StateTransition
            && e.Message.Contains("Paused", StringComparison.Ordinal));

        var endResp = await harness.Worker.HandleAsync(
            new IpcRequestEnvelope(
                IpcCommands.AdminEndPause,
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new AdminEndPauseRequest("hunter2"), IpcJson.Options)),
            CancellationToken.None);
        Assert.True(endResp.Success);

        Assert.Contains(harness.Audit.Entries, e =>
            e.Category == AuditCategory.StateTransition
            && e.Message.Contains("Paused -> Blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disable_and_Enable_emit_StateTransition_audit_entries()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = CheapHasher.Hash("hunter2") });
        await harness.StartAsync();

        var disable = await harness.Worker.HandleAsync(
            new IpcRequestEnvelope(IpcCommands.Disable,
                System.Text.Json.JsonSerializer.SerializeToElement(new DisableRequest("hunter2"), IpcJson.Options)),
            CancellationToken.None);
        Assert.True(disable.Success);

        var enable = await harness.Worker.HandleAsync(
            new IpcRequestEnvelope(IpcCommands.Enable,
                System.Text.Json.JsonSerializer.SerializeToElement(new EnableRequest("hunter2"), IpcJson.Options)),
            CancellationToken.None);
        Assert.True(enable.Success);

        Assert.Contains(harness.Audit.Entries, e =>
            e.Category == AuditCategory.StateTransition
            && e.Message.Contains("Disabled", StringComparison.Ordinal));
        Assert.Contains(harness.Audit.Entries, e =>
            e.Category == AuditCategory.StateTransition
            && e.Message.Contains("Disabled -> Blocked", StringComparison.Ordinal));
    }
}
