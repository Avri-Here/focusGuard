# FocusGuard — Notes for the Next Agent

This repo implements the FocusGuard plan at `.claude/plans/FocusGuard.md`. Read that plan first; this file only records what's already done and what an agent picking up step 3+ needs to know.

## Status (as of 2026-05-20)

- Steps **1–2** of the plan's "Build sequence" are complete.
- Steps **3–12** are open. Resume with step 3.
- All 49 unit tests pass (`dotnet test tests/FocusGuard.Core.Tests/FocusGuard.Core.Tests.csproj`).
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
│   ├── FocusGuard.Service/      # ⬜ skeleton only — Worker template
│   ├── FocusGuard.Tray/         # ⬜ skeleton only — empty WPF
│   └── FocusGuard.Watchdog/     # ⬜ skeleton only — empty console
└── tests/
    ├── FocusGuard.Core.Tests/   # ✅ 49 tests, all green
    └── FocusGuard.Service.Tests/ # ⬜ empty
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

1. **Firewall COM interop is intentionally not wired up.** The Service `.csproj` originally had `<COMReference Include="NetFwTypeLib">` but it fails under `dotnet build` ("ResolveComReference is not supported on the .NET Core version of MSBuild"). Step 4 must use one of:
   - The `WindowsFirewallHelper` NuGet package (preferred — easiest)
   - Late-bound interop via `Type.GetTypeFromProgID("HNetCfg.FwPolicy2")` and `dynamic`
   - Building the Service from Visual Studio (not `dotnet build`)
   The csproj has a comment marker where the reference should go.

2. **DPAPI is `LocalMachine` scope** — fine for the SYSTEM service, but if you ever want to run the Service as a regular user account you'll need to revisit. Tests guard with `Skip.IfNot(IsOSPlatform(Windows))`.

3. **Time-zone independence**: `BudgetClock` does its 7am rollover comparison against `DateTimeOffset.DateTime` (the wall-time component the offset was constructed with), **not** `.LocalDateTime` (which converts to the machine TZ). This is deliberate so tests stay reproducible. Don't "fix" this back to `.LocalDateTime`.

4. **`TreatWarningsAsErrors=true`** is set in `Directory.Build.props` for src projects. Tests opt out via per-project override. Keep src warning-free.

5. **The Tray and Watchdog projects are empty WPF/console templates.** Steps 7–9 will rewrite them.

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
2. Resume at **step 3**: implement `FocusGuard.Service/Program.cs` as a Worker Service that hosts the IPC pipe server, the BudgetClock tick loop, and a no-op `IFirewallManager` placeholder. Get manual install/start working with `sc.exe`.
3. Then **step 4**: implement `FirewallManager.cs` against the Windows Firewall (use `WindowsFirewallHelper` NuGet — see caveat #1).
4. Continue through steps 5–12.

## What is *not* yet decided (questions for the user)

- Whether to use `WindowsFirewallHelper` NuGet vs. late-bound COM for the firewall (recommend NuGet; ask before adding the dependency).
- Whether the WiX installer (step 11) should be built with `WixToolset.Sdk` (modern, .NET-style csproj) or the legacy WiX 3 toolset.
- Code-signing remains out of scope per the plan's "Confirmed decisions" section.
