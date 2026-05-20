# FocusGuard — Notes for the Next Agent

This repo implements the FocusGuard plan at `.claude/plans/FocusGuard.md`. Read that plan first; this file only records what's already done and what an agent picking up step 3+ needs to know.

## Status (as of 2026-05-20)

- Steps **1–5** of the plan's "Build sequence" are complete.
- Steps **6–12** are open. Resume with step 6 (state machine + persistence interactions; right now the Worker already drives posture/firewall/DNS but step 6 is about hardening that across service restarts and clock-tamper, plus the IPC commands beyond Start/Stop budget).
- All tests pass: `dotnet test FocusGuard.slnx` → 49 Core + 33 Service. Firewall smoke tests skip themselves when not elevated or when a real FocusGuard install already owns the `FG-*` rules; the new DNS / adapter tests are pure unit tests with no elevation requirement.
- Solution builds cleanly with `dotnet build FocusGuard.slnx`.

## Environment

- **.NET SDK**: 10.0.300 (only SDK installed). Every project targets `net10.0-windows`. The plan originally said .NET 8; we updated to 10.
- **Solution file**: `FocusGuard.slnx` (XML format) at the repo root. No legacy `FocusGuard.sln`.
- **Working branch**: `main` — the user explicitly consented to working directly on main, no feature branch.
- **Repo location**: `C:\Users\avrahamy\Documents\avriWorkingHere\focusGuard\`. The plan's recommended location (`C:\Intel\workNice\FocusGuard\`) is **not** used.
- **NuGet sources**: `nuget.org` was added explicitly during scaffolding (the machine had only the offline VS source by default). Don't re-add it; check `dotnet nuget list source` first.

## Project layout (already scaffolded)

```
focusGuard/
├── FocusGuard.slnx
├── Directory.Build.props        # Nullable, ImplicitUsings, TreatWarningsAsErrors
├── .gitignore
├── CLAUDE.md                    # this file
├── src/
│   ├── FocusGuard.Core/         # ✅ implemented (see "What Core has")
│   ├── FocusGuard.Service/      # ✅ Worker + PipeServer + FirewallManager + DnsSinkhole + AdapterDnsManager (steps 3–5)
│   ├── FocusGuard.Tray/         # ⬜ skeleton only — empty WPF
│   └── FocusGuard.Watchdog/     # ⬜ skeleton only — empty console
└── tests/
    ├── FocusGuard.Core.Tests/   # ✅ 49 tests, all green
    └── FocusGuard.Service.Tests/ # ✅ Worker + PipeServer + FirewallManager smoke + DnsSinkhole + AdapterDnsManager tests (33 total)
```

There is **no** `FocusGuard.Installer` project yet. WiX MSI is step 11.

## What Core has

| File | What it provides |
|---|---|
| `Ipc/Contracts.cs` | `FocusState` enum; `IpcCommands` constants; `IpcRequestEnvelope` / `IpcResponseEnvelope`; request/response DTOs for every command listed in the plan; `IpcJson.Options`. |
| `Security/PasswordHasher.cs` | Argon2id wrapper. Output format `$argon2id$v=19$m=...,t=...,p=...$<saltB64>$<hashB64>` (unpadded base64). `Hash(pwd)` and `Verify(pwd, encoded)`. |
| `Security/DpapiStore.cs` | `IObjectStore<T>` abstraction. `DpapiStore<T>`: DPAPI-encrypted JSON file, `LocalMachine` scope, atomic write via temp+replace. `InMemoryStore<T>` for tests. Windows-only (uses `[SupportedOSPlatform("windows")]`). |
| `PersistedModels.cs` | `FocusGuardConfig` (passwordHash, whitelist, adminPauseDefaultMinutes) and `FocusGuardState` (cycleStartDate, minutesRemaining, state, pauseEndAt, monotonicAnchor). |
| `StateMachine.cs` | Pure state machine. Inputs are `StateInput` records (StartBudget, StopBudget, BudgetExhausted, AdminPause, AdminEndPause, AdminDisable, AdminEnable, DailyRollover, ClockTamperDetected, PauseExpired). Returns `StateTransition`. Throws `IllegalTransitionException` for disallowed inputs. `PostureFor(state)` maps to `NetworkPosture.Closed/Open`. |
| `IClock.cs` | `IClock` (UtcNow + LocalNow + MonotonicMillis). `SystemClock` real impl. |
| `BudgetClock.cs` | 1Hz tick logic. Drives 7am-local rollover (multi-day catch-up safe), pause expiry, budget consumption, and clock-tamper detection (>5 min backward jump vs. monotonic). Returns `BudgetTickResult` with `BudgetTickEvent` flags. **Stateless w.r.t. side effects** — caller wires events into the state machine. |

Tests use `FakeClock` (`tests/FocusGuard.Core.Tests/FakeClock.cs`) that lets you advance wall and monotonic independently.

## Important design contract for step 3+

**The Core layer is intentionally pure.** It never touches:
- the registry, filesystem (except `DpapiStore`), firewall, network, or COM
- `DateTimeOffset.Now` or `Environment.TickCount64` directly — always go through `IClock`

The Service host (step 3) is responsible for:
- owning the timer that calls `BudgetClock.Tick` once per second
- translating `BudgetTickEvent` flags into `StateInput` calls on `StateMachine`
- reacting to the resulting `StateTransition` (firewall posture flip, audit log, persist state)
- persisting `FocusGuardState` every 5s and on every transition (already shaped by `BudgetClock.Snapshot`)

Don't smuggle business logic into the Service; keep it as a thin wrapper.

## Known caveats / gotchas for the next agent

1. **Firewall is via `WindowsFirewallHelper` NuGet** (`FirewallWAS.Instance` under the hood). The original COM-reference approach fails under `dotnet build` — don't try to revive it. `FirewallManager.EnsureStaticRules` is idempotent; calling it twice never duplicates rules. Anything created or refreshed via this manager is named with the `FG-` prefix and grouped as `FocusGuard`. `ApplyPosture` only toggles `FG-BlockAll`'s `IsEnable` — the open posture leaves the other allow rules in place but moot.

2. **DPAPI is `LocalMachine` scope** — fine for the SYSTEM service, but if you ever want to run the Service as a regular user account you'll need to revisit. Tests guard with `Skip.IfNot(IsOSPlatform(Windows))`.

3. **Time-zone independence**: `BudgetClock` does its 7am rollover comparison against `DateTimeOffset.DateTime` (the wall-time component the offset was constructed with), **not** `.LocalDateTime` (which converts to the machine TZ). This is deliberate so tests stay reproducible. Don't "fix" this back to `.LocalDateTime`.

4. **`TreatWarningsAsErrors=true`** is set in `Directory.Build.props` for src projects. Tests opt out via per-project override. Keep src warning-free.

5. **The Tray and Watchdog projects are empty WPF/console templates.** Steps 7–9 will rewrite them.

6. **DNS sinkhole** (`DnsSinkhole.cs`) is split into a pure policy core (`HandleQueryAsync`, `SweepExpired`, `SetPosture`) and a Windows-only socket binder (`Start`/`Stop`) that hands ARSoft `QueryReceivedEventArgs` through `HandleQueryAsync`. Unit tests drive only the policy core — they never bind UDP/53. Whitelist matching is **suffix-based** (`example.com` matches `example.com` and `*.example.com`, but NOT `notexample.com`). Open posture forwards everything upstream and skips `UpsertAllowIp`. Closed posture NXDOMAINs anything not whitelisted. The Worker calls `SweepExpired` once per tick.

7. **AdapterDnsManager** captures originals **lazily** the first time `OverrideToLoopback` is called (or when a new adapter appears later). The originals are written into `FocusGuardConfig.SavedAdapterDns` so they survive a service restart. On admin Disable the Worker restores them and stops the sinkhole; on re-Enable it re-pushes loopback and restarts the sinkhole. The production WMI backend (`WmiNetworkAdapterBackend`) is exercised only by manual VM verification — unit tests use `INetworkAdapterBackend` with an in-memory fake.

8. **Upstream DNS** is configurable via `ServiceOptions.UpstreamDns` (default `["1.1.1.1", "1.0.0.1"]`). To override at install time, set `FocusGuard:UpstreamDns:0` etc. via env-var, appsettings, or `sc config` arguments — `Host.CreateApplicationBuilder` binds the section automatically.

## Useful commands

```powershell
# Build the entire solution
dotnet build FocusGuard.slnx

# Run the Core tests
dotnet test tests\FocusGuard.Core.Tests\FocusGuard.Core.Tests.csproj

# Run a single test
dotnet test tests\FocusGuard.Core.Tests\FocusGuard.Core.Tests.csproj --filter "FullyQualifiedName~Rollover"

# Restore (rarely needed — build does this)
dotnet restore FocusGuard.slnx
```

## Recommended order for the next session

The plan's build sequence is good as-is. Next agent should:

1. Open `.claude/plans/FocusGuard.md` and tick what's done (already ticked there).
2. Resume at **step 6**: harden state-machine + persistence interactions across service restart, clock-tamper, and the remaining IPC commands (`SetPassword`, `AddWhitelist`, `RemoveWhitelist`, `AdminPause`, `AdminEndPause`, `Disable`, `Enable`). The DNS sinkhole + adapter manager already react to posture changes; what's missing is the *admin* command surface that drives those transitions.
3. Continue through steps 7–12.

### Manual install / start (after a Release build)

```powershell
# Build self-contained or framework-dependent — either is fine for local testing.
dotnet publish src/FocusGuard.Service/FocusGuard.Service.csproj -c Release -r win-x64 --self-contained false -o C:\ProgramData\FocusGuard\bin

# Register with the SCM as a SYSTEM service.
sc.exe create FocusGuard binPath= "C:\ProgramData\FocusGuard\bin\FocusGuard.Service.exe" start= auto obj= LocalSystem DisplayName= "FocusGuard"
sc.exe description FocusGuard "FocusGuard internet self-block service"
sc.exe failure FocusGuard reset= 0 actions= restart/5000/restart/5000/restart/5000
sc.exe start FocusGuard

# Smoke-test the IPC pipe (PowerShell):
$p = New-Object System.IO.Pipes.NamedPipeClientStream('.','focusguard.cmd','InOut')
$p.Connect(2000)
# (use FocusGuard.Service.Ipc.PipeClient from a tiny test exe for full round-trip)
```

The service's data directory is `C:\ProgramData\FocusGuard\` — the host creates it on first launch. EventLog source `FocusGuard` receives lifecycle entries.

## What is *not* yet decided (questions for the user)

- Whether the WiX installer (step 11) should be built with `WixToolset.Sdk` (modern, .NET-style csproj) or the legacy WiX 3 toolset.
- Code-signing remains out of scope per the plan's "Confirmed decisions" section.
