using System.Runtime.Versioning;
using System.Text.Json;
using FocusGuard.Core;
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
    IObjectStore<FocusGuardConfig> configStore,
    IObjectStore<FocusGuardState> stateStore,
    IOptions<ServiceOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<Worker> logger) : BackgroundService, ICommandHandler
{
    private readonly ServiceOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FocusGuardConfig _config = new();
    private StateMachine _stateMachine = new();
    private BudgetClock _budgetClock = null!;
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset _lastPersistAt = DateTimeOffset.MinValue;

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

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Apply a <see cref="StateInput"/> within the gate. Returns true if the state changed.</summary>
    private bool ApplyInput(StateInput input, bool persistImmediately)
    {
        var previous = _stateMachine.Current;
        var transition = _stateMachine.Apply(input);

        if (previous != transition.To)
        {
            firewall.ApplyPosture(transition.Posture);

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
                IpcCommands.SetPassword => NotImplemented(request.Command),
                IpcCommands.AddWhitelist => NotImplemented(request.Command),
                IpcCommands.RemoveWhitelist => NotImplemented(request.Command),
                IpcCommands.AdminPause => NotImplemented(request.Command),
                IpcCommands.AdminEndPause => NotImplemented(request.Command),
                IpcCommands.Disable => NotImplemented(request.Command),
                IpcCommands.Enable => NotImplemented(request.Command),
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

    private static IpcResponseEnvelope NotImplemented(string command) =>
        new(false, $"{command} not implemented in service skeleton", null);

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
