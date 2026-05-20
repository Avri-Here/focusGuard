using FocusGuard.Core;
using FocusGuard.Core.Audit;
using FocusGuard.Core.Ipc;
using FocusGuard.Core.Security;
using FocusGuard.Service;
using FocusGuard.Service.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Builds a <see cref="Worker"/> wired up with in-memory stores, a recording firewall, and a
/// fake clock — everything tests need to drive the worker deterministically without touching
/// disk, the registry, or the Windows Firewall.
/// </summary>
internal sealed class WorkerHarness
{
    public FakeClock Clock { get; }
    public RecordingFirewall Firewall { get; }
    public RecordingSinkhole Sinkhole { get; }
    public RecordingAdapterManager Adapters { get; }
    public InMemoryStore<FocusGuardConfig> ConfigStore { get; }
    public InMemoryStore<FocusGuardState> StateStore { get; }
    public RecordingAuditLog Audit { get; }
    public PasswordHasher Hasher { get; }
    public ServiceOptions Options { get; }
    public Worker Worker { get; }

    private WorkerHarness(FakeClock clock, RecordingFirewall fw, RecordingSinkhole sh, RecordingAdapterManager ad, InMemoryStore<FocusGuardConfig> cs, InMemoryStore<FocusGuardState> ss, RecordingAuditLog audit, PasswordHasher hasher, ServiceOptions opts, Worker w)
    {
        Clock = clock;
        Firewall = fw;
        Sinkhole = sh;
        Adapters = ad;
        ConfigStore = cs;
        StateStore = ss;
        Audit = audit;
        Hasher = hasher;
        Options = opts;
        Worker = w;
    }

    public static WorkerHarness Build(
        FocusGuardConfig? seedConfig = null,
        FocusGuardState? seedState = null,
        DateTimeOffset? localNow = null,
        string? pipeName = null)
    {
        var clock = new FakeClock(localNow ?? new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var firewall = new RecordingFirewall();
        var sinkhole = new RecordingSinkhole();
        var adapters = new RecordingAdapterManager();
        var audit = new RecordingAuditLog();
        // Use cheap Argon2id parameters to keep tests fast.
        var hasher = new PasswordHasher(new Argon2idParams(MemoryKb: 8, Iterations: 1, Parallelism: 1, HashLengthBytes: 16, SaltLengthBytes: 8));
        var configStore = new InMemoryStore<FocusGuardConfig>();
        if (seedConfig is not null) configStore.Save(seedConfig);
        var stateStore = new InMemoryStore<FocusGuardState>();
        if (seedState is not null) stateStore.Save(seedState);

        var opts = new ServiceOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "FocusGuardTests-" + Guid.NewGuid().ToString("N")),
            PipeName = pipeName ?? "focusguard.test." + Guid.NewGuid().ToString("N"),
        };

        var worker = new Worker(
            clock: clock,
            firewall: firewall,
            sinkhole: sinkhole,
            adapters: adapters,
            configStore: configStore,
            stateStore: stateStore,
            audit: audit,
            hasher: hasher,
            options: Microsoft.Extensions.Options.Options.Create(opts),
            loggerFactory: NullLoggerFactory.Instance,
            logger: NullLogger<Worker>.Instance);

        return new WorkerHarness(clock, firewall, sinkhole, adapters, configStore, stateStore, audit, hasher, opts, worker);
    }

    public Task StartAsync() => Worker.StartAsync(CancellationToken.None);
}

internal sealed class RecordingFirewall : IFirewallManager
{
    public List<NetworkPosture> PostureCalls { get; } = new();
    public bool StaticRulesInstalled { get; private set; }
    public List<(string Ip, TimeSpan Ttl)> Upserts { get; } = new();
    public List<string> Removes { get; } = new();

    public NetworkPosture? LastPosture => PostureCalls.Count == 0 ? null : PostureCalls[^1];

    public void EnsureStaticRules() => StaticRulesInstalled = true;
    public void ApplyPosture(NetworkPosture posture) => PostureCalls.Add(posture);
    public void UpsertAllowIp(string ip, TimeSpan ttl) => Upserts.Add((ip, ttl));
    public void RemoveAllowIp(string ip) => Removes.Add(ip);
}

internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new();

    public void Append(AuditCategory category, string message) => Entries.Add(new AuditEntry(category, message));
}

internal sealed record AuditEntry(AuditCategory Category, string Message);
