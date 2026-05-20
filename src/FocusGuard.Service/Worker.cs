using System.Runtime.Versioning;
using System.Text.Json;
using FocusGuard.Core;
using FocusGuard.Core.Audit;
using FocusGuard.Core.Ipc;
using FocusGuard.Core.Security;
using FocusGuard.Service.Ipc;
using FocusGuard.Service.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusGuard.Service;

/// <summary>
/// Orchestrates the FocusGuard core inside the service host. Responsibilities:
/// <list type="bullet">
///   <item>own the 1Hz tick loop driving <see cref="BudgetClock"/></item>
///   <item>translate <see cref="BudgetTickEvent"/> flags into <see cref="StateMachine"/> inputs</item>
///   <item>apply the resulting <see cref="NetworkPosture"/> via <see cref="IFirewallManager"/></item>
///   <item>persist <see cref="FocusGuardState"/> on every transition and every <see cref="ServiceOptions.StatePersistInterval"/></item>
///   <item>serve IPC commands over <see cref="PipeServer"/> by acting as <see cref="ICommandHandler"/></item>
/// </list>
/// All state mutation goes through <see cref="_gate"/> so a tick and an IPC command can never
/// race each other. The Core layer remains pure; this is the only place side-effects live.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Worker(
    IClock clock,
    IFirewallManager firewall,
    IDnsSinkhole sinkhole,
    IAdapterDnsManager adapters,
    IObjectStore<FocusGuardConfig> configStore,
    IObjectStore<FocusGuardState> stateStore,
    IAuditLog audit,
    PasswordHasher hasher,
    ISessionLauncher sessionLauncher,
    IOptions<ServiceOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<Worker> logger) : BackgroundService, ICommandHandler
{
    private readonly ServiceOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AuthLockout _lockout = new(clock);

    private FocusGuardConfig _config = new();
    private StateMachine _stateMachine = new();
    private BudgetClock _budgetClock = null!;
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset _lastPersistAt = DateTimeOffset.MinValue;

    // Watchdog supervision: track the PID we last launched, and the last attempt time so we
    // throttle relaunches to WatchdogInterval — see TickOnceAsync's tail.
    private int? _watchdogPid;
    private DateTimeOffset _lastWatchdogAttempt = DateTimeOffset.MinValue;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);

        _config = configStore.Load() ?? new FocusGuardConfig();
        if (!configStore.Exists())
        {
            configStore.Save(_config);
            logger.LogInformation("Initialised default config at {Path}", _options.ConfigPath);
        }

        var persisted = stateStore.Load();
        if (persisted is null)
        {
            persisted = new FocusGuardState
            {
                CycleStartDate = DateOnly.FromDateTime(clock.LocalNow.DateTime),
                MinutesRemaining = BudgetClock.DailyBudgetMinutes,
                State = FocusState.Blocked,
            };
            stateStore.Save(persisted);
            logger.LogInformation("Initialised default state at {Path}", _options.StatePath);
        }

        _stateMachine = new StateMachine(persisted.State, persisted.PauseEndAt);
        _budgetClock = BudgetClock.Restore(clock, persisted);

        firewall.EnsureStaticRules();
        firewall.ApplyPosture(_stateMachine.CurrentPosture);

        // Push 127.0.0.1 onto every adapter so the sinkhole intercepts every query — but only
        // when we're not in Disabled, where the user expects the machine to be unmodified.
        if (_stateMachine.Current != FocusState.Disabled)
        {
            var captured = adapters.OverrideToLoopback();
            MergeSavedAdapterDns(captured);
        }
        sinkhole.SetPosture(_stateMachine.CurrentPosture);
        sinkhole.Start();

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { sinkhole.Stop(); } catch { /* best effort */ }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void MergeSavedAdapterDns(IReadOnlyDictionary<string, IReadOnlyList<string>> captured)
    {
        var changed = false;
        foreach (var (id, original) in captured)
        {
            if (!_config.SavedAdapterDns.TryGetValue(id, out var existing)
                || !existing.SequenceEqual(original))
            {
                _config.SavedAdapterDns[id] = original.ToList();
                changed = true;
            }
        }
        if (changed) configStore.Save(_config);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeServer = new PipeServer(_options.PipeName, this, loggerFactory.CreateLogger<PipeServer>());
        var pipeTask = pipeServer.RunAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(_options.TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        await pipeTask.ConfigureAwait(false);
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = _budgetClock.Tick(_stateMachine.Current, _stateMachine.PauseEndAt);
            var transitioned = false;

            if (result.Has(BudgetTickEvent.ClockTamperDetected) && _stateMachine.Current == FocusState.Browsing)
                transitioned |= ApplyInput(new StateInput.ClockTamperDetected(), persistImmediately: false);

            if (result.Has(BudgetTickEvent.DailyRollover))
            {
                if (_stateMachine.Current == FocusState.Browsing || _stateMachine.Current == FocusState.Disabled || _stateMachine.Current == FocusState.Paused || _stateMachine.Current == FocusState.Blocked)
                    transitioned |= ApplyInput(new StateInput.DailyRollover(), persistImmediately: false);
            }

            if (result.Has(BudgetTickEvent.PauseExpired) && _stateMachine.Current == FocusState.Paused)
                transitioned |= ApplyInput(new StateInput.PauseExpired(), persistImmediately: false);

            if (result.Has(BudgetTickEvent.BudgetExhausted) && _stateMachine.Current == FocusState.Browsing)
                transitioned |= ApplyInput(new StateInput.BudgetExhausted(), persistImmediately: false);

            sinkhole.SweepExpired();
            SuperviseWatchdog();

            var now = clock.UtcNow;
            if (transitioned || now - _lastPersistAt >= _options.StatePersistInterval)
            {
                Persist();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Best-effort: if we're not Disabled, no user is logged on, or we've recently tried,
    /// do nothing. Otherwise verify the previously-launched PID is still alive and relaunch
    /// the watchdog into the active console session if needed.
    /// </summary>
    private void SuperviseWatchdog()
    {
        if (_stateMachine.Current == FocusState.Disabled) return;

        var now = clock.UtcNow;
        if (now - _lastWatchdogAttempt < _options.WatchdogInterval) return;

        if (_watchdogPid is { } pid && IsProcessAlive(pid))
            return;

        _lastWatchdogAttempt = now;

        if (!sessionLauncher.HasInteractiveUser())
        {
            _watchdogPid = null;
            return;
        }

        var exePath = Path.Combine(AppContext.BaseDirectory, _options.WatchdogExeName);
        if (!File.Exists(exePath))
        {
            logger.LogDebug("Watchdog exe not found at {ExePath} — skipping launch", exePath);
            _watchdogPid = null;
            return;
        }

        try
        {
            _watchdogPid = sessionLauncher.Launch(exePath);
        }
        catch (Exception ex)
        {
            // SessionLauncher is documented as never-throwing, but be defensive.
            logger.LogWarning(ex, "Watchdog launch threw");
            _watchdogPid = null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Apply a <see cref="StateInput"/> within the gate. Returns true if the state changed.</summary>
    private bool ApplyInput(StateInput input, bool persistImmediately)
    {
        var previous = _stateMachine.Current;
        var transition = _stateMachine.Apply(input);

        if (previous != transition.To)
        {
            firewall.ApplyPosture(transition.Posture);
            sinkhole.SetPosture(transition.Posture);

            // Disable returns the user's adapter DNS to its originals; re-enable repushes loopback.
            if (transition.To == FocusState.Disabled && previous != FocusState.Disabled)
            {
                RestoreAdaptersFromConfig();
                sinkhole.Stop();
            }
            else if (previous == FocusState.Disabled && transition.To != FocusState.Disabled)
            {
                var captured = adapters.OverrideToLoopback();
                MergeSavedAdapterDns(captured);
                sinkhole.Start();
            }

            if (transition.To == FocusState.Browsing)
                _sessionStartedAt = clock.UtcNow;
            else if (previous == FocusState.Browsing)
                _sessionStartedAt = null;

            logger.LogInformation("Transition {From} -> {To} ({Reason})", transition.From, transition.To, transition.Reason);

            if (persistImmediately) Persist();
            return true;
        }

        return false;
    }

    private void RestoreAdaptersFromConfig()
    {
        if (_config.SavedAdapterDns.Count == 0) return;
        var snapshot = _config.SavedAdapterDns
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToArray());
        adapters.Restore(snapshot);
    }

    private void Persist()
    {
        var snapshot = _budgetClock.Snapshot(_stateMachine.Current, _stateMachine.PauseEndAt);
        stateStore.Save(snapshot);
        _lastPersistAt = clock.UtcNow;
    }

    // ---- ICommandHandler ----

    public async Task<IpcResponseEnvelope> HandleAsync(IpcRequestEnvelope request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return request.Command switch
            {
                IpcCommands.GetStatus => Ok(BuildStatus()),
                IpcCommands.StartBudget => HandleStartBudget(),
                IpcCommands.StopBudget => HandleStopBudget(),
                IpcCommands.SetPassword => HandleSetPassword(Parse<SetPasswordRequest>(request.Payload)),
                IpcCommands.AddWhitelist => HandleAddWhitelist(Parse<AddWhitelistRequest>(request.Payload)),
                IpcCommands.RemoveWhitelist => HandleRemoveWhitelist(Parse<RemoveWhitelistRequest>(request.Payload)),
                IpcCommands.AdminPause => HandleAdminPause(Parse<AdminPauseRequest>(request.Payload)),
                IpcCommands.AdminEndPause => HandleAdminEndPause(Parse<AdminEndPauseRequest>(request.Payload)),
                IpcCommands.Disable => HandleDisable(Parse<DisableRequest>(request.Payload)),
                IpcCommands.Enable => HandleEnable(Parse<EnableRequest>(request.Payload)),
                _ => new IpcResponseEnvelope(false, $"unknown command: {request.Command}", null),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private IpcResponseEnvelope HandleStartBudget()
    {
        if (_stateMachine.Current != FocusState.Blocked)
            return new IpcResponseEnvelope(false, $"cannot start budget while {_stateMachine.Current}", null);
        if (_budgetClock.MinutesRemaining <= 0)
            return new IpcResponseEnvelope(false, "no minutes remaining in this cycle", null);
        if (string.IsNullOrEmpty(_config.PasswordHash))
            return new IpcResponseEnvelope(false, "admin password must be set before browsing", null);

        ApplyInput(new StateInput.StartBudget(_budgetClock.MinutesRemaining), persistImmediately: true);
        return Ok(BuildStatus());
    }

    private IpcResponseEnvelope HandleStopBudget()
    {
        if (_stateMachine.Current != FocusState.Browsing)
            return new IpcResponseEnvelope(false, "no active session", null);

        ApplyInput(new StateInput.StopBudget(), persistImmediately: true);
        return Ok(BuildStatus());
    }

    // ---- Password-gated commands ----

    private IpcResponseEnvelope HandleSetPassword(SetPasswordRequest req)
    {
        if (_lockout.IsLockedOut())
            return Locked();

        // First-time setup: empty old password is accepted iff no password is configured yet.
        var firstTime = string.IsNullOrEmpty(_config.PasswordHash);
        if (!firstTime && !hasher.Verify(req.OldPassword ?? string.Empty, _config.PasswordHash!))
            return AuthFailed("SetPassword");

        if (string.IsNullOrEmpty(req.NewPassword))
            return new IpcResponseEnvelope(false, "new password must not be empty", null);

        _config.PasswordHash = hasher.Hash(req.NewPassword);
        configStore.Save(_config);
        _lockout.RecordSuccess();
        audit.Append(AuditCategory.AdminAction, firstTime ? "Password set (first time)" : "Password rotated");
        return Ok(new OkResponse());
    }

    private IpcResponseEnvelope HandleAddWhitelist(AddWhitelistRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        if (!VerifyAdmin(req.Password, "AddWhitelist", out var fail)) return fail;

        if (!TryNormalizeDomain(req.Domain, out var normalized))
            return new IpcResponseEnvelope(false, "invalid domain", null);

        if (!_config.Whitelist.Contains(normalized, StringComparer.Ordinal))
        {
            _config.Whitelist.Add(normalized);
            configStore.Save(_config);
        }
        audit.Append(AuditCategory.AdminAction, $"Whitelist add: {normalized}");
        return Ok(new OkResponse());
    }

    private IpcResponseEnvelope HandleRemoveWhitelist(RemoveWhitelistRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        if (!VerifyAdmin(req.Password, "RemoveWhitelist", out var fail)) return fail;

        if (!TryNormalizeDomain(req.Domain, out var normalized))
            return new IpcResponseEnvelope(false, "invalid domain", null);

        if (_config.Whitelist.RemoveAll(d => string.Equals(d, normalized, StringComparison.Ordinal)) > 0)
            configStore.Save(_config);
        audit.Append(AuditCategory.AdminAction, $"Whitelist remove: {normalized}");
        return Ok(new OkResponse());
    }

    private IpcResponseEnvelope HandleAdminPause(AdminPauseRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        if (!VerifyAdmin(req.Password, "AdminPause", out var fail)) return fail;

        if (_stateMachine.Current != FocusState.Blocked && _stateMachine.Current != FocusState.Browsing)
            return new IpcResponseEnvelope(false, $"cannot pause from {_stateMachine.Current}", null);

        var minutes = req.DurationMinutes > 0 ? req.DurationMinutes : _config.AdminPauseDefaultMinutes;
        var until = clock.UtcNow + TimeSpan.FromMinutes(minutes);
        ApplyInput(new StateInput.AdminPause(until), persistImmediately: true);
        audit.Append(AuditCategory.AdminAction, $"AdminPause {minutes}min");
        return Ok(BuildStatus());
    }

    private IpcResponseEnvelope HandleAdminEndPause(AdminEndPauseRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        if (!VerifyAdmin(req.Password, "AdminEndPause", out var fail)) return fail;

        if (_stateMachine.Current != FocusState.Paused)
            return new IpcResponseEnvelope(false, "not currently paused", null);

        ApplyInput(new StateInput.AdminEndPause(), persistImmediately: true);
        audit.Append(AuditCategory.AdminAction, "AdminEndPause");
        return Ok(BuildStatus());
    }

    private IpcResponseEnvelope HandleDisable(DisableRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        // Mirror StartBudget: refuse to disable without a configured password — otherwise anyone
        // could pop the firewall open before setup is complete.
        if (string.IsNullOrEmpty(_config.PasswordHash))
            return new IpcResponseEnvelope(false, "admin password must be set before disabling", null);
        if (!VerifyAdmin(req.Password, "Disable", out var fail)) return fail;

        if (_stateMachine.Current == FocusState.Disabled)
            return Ok(BuildStatus());

        ApplyInput(new StateInput.AdminDisable(), persistImmediately: true);
        audit.Append(AuditCategory.AdminAction, "Disable");
        return Ok(BuildStatus());
    }

    private IpcResponseEnvelope HandleEnable(EnableRequest req)
    {
        if (_lockout.IsLockedOut()) return Locked();
        if (!VerifyAdmin(req.Password, "Enable", out var fail)) return fail;

        if (_stateMachine.Current != FocusState.Disabled)
            return new IpcResponseEnvelope(false, "not currently disabled", null);

        ApplyInput(new StateInput.AdminEnable(), persistImmediately: true);
        audit.Append(AuditCategory.AdminAction, "Enable");
        return Ok(BuildStatus());
    }

    private bool VerifyAdmin(string? password, string command, out IpcResponseEnvelope failure)
    {
        if (string.IsNullOrEmpty(_config.PasswordHash))
        {
            failure = new IpcResponseEnvelope(false, "admin password must be set first", null);
            return false;
        }
        if (!hasher.Verify(password ?? string.Empty, _config.PasswordHash))
        {
            failure = AuthFailed(command);
            return false;
        }
        _lockout.RecordSuccess();
        failure = default!;
        return true;
    }

    private IpcResponseEnvelope AuthFailed(string command)
    {
        _lockout.RecordFailure();
        audit.Append(AuditCategory.AuthFailure, $"{command}: wrong password");
        return new IpcResponseEnvelope(false, "incorrect password", null);
    }

    private IpcResponseEnvelope Locked()
    {
        var seconds = (int)Math.Ceiling(_lockout.TimeRemaining().TotalSeconds);
        return new IpcResponseEnvelope(false, $"locked out, try again in {seconds}s", null);
    }

    /// <summary>
    /// Domain validation: lowercase + strip a single trailing dot. Reject anything that looks
    /// like a URL or otherwise can't be a bare hostname (slash, scheme, whitespace, no dot).
    /// </summary>
    internal static bool TryNormalizeDomain(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (trimmed.Contains('/') || trimmed.Contains(' ') || trimmed.Contains('\t')) return false;
        if (trimmed.Contains("://", StringComparison.Ordinal)) return false;
        if (!trimmed.Contains('.')) return false;

        var lowered = trimmed.ToLowerInvariant();
        if (lowered.EndsWith('.')) lowered = lowered[..^1];
        if (lowered.Length == 0 || !lowered.Contains('.')) return false;
        normalized = lowered;
        return true;
    }

    private static T Parse<T>(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
            return Activator.CreateInstance<T>()!;
        return JsonSerializer.Deserialize<T>(payload.GetRawText(), IpcJson.Options)
               ?? Activator.CreateInstance<T>()!;
    }

    private StatusResponse BuildStatus()
    {
        var sessionSeconds = _sessionStartedAt is { } start
            ? Math.Max(0, (int)(clock.UtcNow - start).TotalSeconds)
            : 0;

        return new StatusResponse(
            State: _stateMachine.Current,
            MinutesRemaining: _budgetClock.MinutesRemaining,
            SecondsThisSession: sessionSeconds,
            PauseEndAt: _stateMachine.PauseEndAt,
            Whitelist: _config.Whitelist.AsReadOnly(),
            CycleResetAt: _budgetClock.NextCycleResetLocal(clock.LocalNow.Offset),
            RequiresPasswordSetup: string.IsNullOrEmpty(_config.PasswordHash));
    }

    private static IpcResponseEnvelope Ok<T>(T result)
    {
        var element = JsonSerializer.SerializeToElement(result, IpcJson.Options);
        return new IpcResponseEnvelope(true, null, element);
    }

    /// <summary>Test seam: drive a single tick deterministically.</summary>
    public Task TickForTestsAsync(CancellationToken ct = default) => TickOnceAsync(ct);

    /// <summary>Test seam: read a status snapshot without going through the pipe.</summary>
    public async Task<StatusResponse> SnapshotForTestsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return BuildStatus(); }
        finally { _gate.Release(); }
    }
}
