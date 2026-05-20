using FocusGuard.Core;
using FocusGuard.Core.Ipc;
using FocusGuard.Service.Network;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Tests that the <see cref="Worker"/> drives <see cref="IDnsSinkhole"/> and
/// <see cref="IAdapterDnsManager"/> through their lifecycle on startup, posture change, and on
/// transitions in/out of the Disabled state.
/// </summary>
public class WorkerDnsWiringTests
{
    [Fact]
    public async Task StartAsync_overrides_adapter_dns_and_sets_initial_posture_on_sinkhole()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 30,
                State = FocusState.Blocked,
            });

        await harness.StartAsync();

        Assert.Equal(1, harness.Adapters.OverrideCalls);
        Assert.Equal(NetworkPosture.Closed, harness.Sinkhole.LastPosture);
        Assert.True(harness.Sinkhole.Started);
    }

    [Fact]
    public async Task StartBudget_flips_sinkhole_posture_open_and_back_to_closed_on_StopBudget()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." });
        await harness.StartAsync();

        await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StartBudget, default), CancellationToken.None);
        Assert.Equal(NetworkPosture.Open, harness.Sinkhole.LastPosture);

        await harness.Worker.HandleAsync(new IpcRequestEnvelope(IpcCommands.StopBudget, default), CancellationToken.None);
        Assert.Equal(NetworkPosture.Closed, harness.Sinkhole.LastPosture);
    }

    [Fact]
    public async Task Tick_invokes_periodic_sweep_on_sinkhole()
    {
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." });
        await harness.StartAsync();

        var sweepsBefore = harness.Sinkhole.SweepCount;
        await harness.Worker.TickForTestsAsync();

        Assert.True(harness.Sinkhole.SweepCount > sweepsBefore);
    }
}

internal sealed class RecordingSinkhole : IDnsSinkhole
{
    public NetworkPosture? LastPosture { get; private set; }
    public int SweepCount { get; private set; }
    public bool Started { get; private set; }

    public void SetPosture(NetworkPosture posture) => LastPosture = posture;
    public void SweepExpired() => SweepCount++;
    public void Start() => Started = true;
    public void Stop() => Started = false;
}

internal sealed class RecordingAdapterManager : IAdapterDnsManager
{
    public int OverrideCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public Dictionary<string, IReadOnlyList<string>> LastOriginals { get; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideToLoopback()
    {
        OverrideCalls++;
        return new Dictionary<string, IReadOnlyList<string>>
        {
            ["{fake}"] = new[] { "8.8.8.8" },
        };
    }

    public void Restore(IReadOnlyDictionary<string, IReadOnlyList<string>> originals)
    {
        RestoreCalls++;
        LastOriginals.Clear();
        foreach (var (k, v) in originals) LastOriginals[k] = v;
    }
}
