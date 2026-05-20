using FocusGuard.Core;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Service.Tests;

public class WorkerTests
{
    [Fact]
    public async Task StartAsync_initialises_static_rules_and_applies_posture_for_persisted_state()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 47.0,
                State = FocusState.Blocked,
            });

        await harness.StartAsync();

        Assert.True(harness.Firewall.StaticRulesInstalled);
        Assert.Equal(NetworkPosture.Closed, harness.Firewall.LastPosture);
    }

    [Fact]
    public async Task StartAsync_seeds_default_state_when_store_is_empty()
    {
        var harness = WorkerHarness.Build();

        await harness.StartAsync();

        var loaded = harness.StateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(FocusState.Blocked, loaded!.State);
        Assert.Equal(BudgetClock.DailyBudgetMinutes, loaded.MinutesRemaining);
    }

    [Fact]
    public async Task GetStatus_returns_current_snapshot()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig
            {
                PasswordHash = "$argon2id$...",
                Whitelist = { "example.com" },
            },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 42.0,
                State = FocusState.Blocked,
            });
        await harness.StartAsync();

        var snapshot = await harness.Worker.SnapshotForTestsAsync();

        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.Equal(42.0, snapshot.MinutesRemaining);
        Assert.Contains("example.com", snapshot.Whitelist);
        Assert.False(snapshot.RequiresPasswordSetup);
    }

    [Fact]
    public async Task RequiresPasswordSetup_is_true_when_config_has_no_hash()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var snapshot = await harness.Worker.SnapshotForTestsAsync();

        Assert.True(snapshot.RequiresPasswordSetup);
    }

    [Fact]
    public async Task StartBudget_refuses_when_no_password_is_set()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var response = await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StartBudget, default), CancellationToken.None);

        Assert.False(response.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
    }

    [Fact]
    public async Task StartBudget_transitions_to_browsing_and_flips_posture_open()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." });
        await harness.StartAsync();

        var response = await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StartBudget, default), CancellationToken.None);

        Assert.True(response.Success);
        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Browsing, snapshot.State);
        Assert.Equal(NetworkPosture.Open, harness.Firewall.LastPosture);
    }

    [Fact]
    public async Task Tick_consumes_budget_and_exhaustion_drives_back_to_blocked()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 0.05,
                State = FocusState.Blocked,
            });
        await harness.StartAsync();

        await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StartBudget, default), CancellationToken.None);
        Assert.Equal(NetworkPosture.Open, harness.Firewall.LastPosture);

        // 0.05 minutes = 3 seconds; one 4s tick is more than enough to drain.
        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        await harness.Worker.TickForTestsAsync();

        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(FocusState.Blocked, snapshot.State);
        Assert.Equal(0.0, snapshot.MinutesRemaining);
        Assert.Equal(NetworkPosture.Closed, harness.Firewall.LastPosture);
    }

    [Fact]
    public async Task Tick_persists_state_after_transition()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 0.05,
                State = FocusState.Blocked,
            });
        await harness.StartAsync();

        await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StartBudget, default), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromSeconds(4));
        await harness.Worker.TickForTestsAsync();

        var persisted = harness.StateStore.Load();
        Assert.NotNull(persisted);
        Assert.Equal(FocusState.Blocked, persisted!.State);
        Assert.Equal(0.0, persisted.MinutesRemaining);
    }

    [Fact]
    public async Task Tick_at_7am_rolls_cycle_over_and_refills_budget()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 19),
                MinutesRemaining = 12.0,
                State = FocusState.Blocked,
            },
            localNow: new DateTimeOffset(2026, 5, 20, 6, 59, 30, TimeSpan.FromHours(3)));
        await harness.StartAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Worker.TickForTestsAsync();

        var snapshot = await harness.Worker.SnapshotForTestsAsync();
        Assert.Equal(BudgetClock.DailyBudgetMinutes, snapshot.MinutesRemaining);
        Assert.Equal(FocusState.Blocked, snapshot.State);
    }

    [Fact]
    public async Task Unknown_command_returns_error_envelope()
    {
        var harness = WorkerHarness.Build();
        await harness.StartAsync();

        var response = await harness.Worker.HandleAsync(new IpcRequestEnvelope("BogusCommand", default), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("unknown command", response.Error);
    }

}
