# FocusGuard — Notes for the Next Agent

This repo implements the FocusGuard plan at `.claude/plans/FocusGuard.md`. Read that plan first; this file only records what's already done and what an agent picking up step 3+ needs to know.

## Status (as of 2026-05-20)

- Steps **1–12** of the plan's "Build sequence" are complete. **Code-complete v1.**
- The remaining work before shipping is the **manual VM verification** itself (the runbook is at `VERIFICATION.md`) and any follow-up hardening uncovered there.
- Open TODOs intentionally deferred:
  - **Uninstall password guard** (step 11): structural hooks landed in `Product.wxs` but the managed-CA prompt is not wired. Documented inline + in VERIFICATION.md.
  - **Live-fire DNS smoke test**: the sinkhole has no end-to-end test that actually binds UDP/53 (unit tests drive the policy core only). Spot-checked manually — see plan step 5.
  - **Code signing**: skipped per the plan's "Confirmed decisions". MSI is unsigned.
- All tests pass: `dotnet test FocusGuard.slnx` → 59 Core + 70 Service (= **129 total**). Firewall smoke tests skip themselves when not elevated or when a real FocusGuard install already owns the `FG-*` rules; SessionLauncher smoke tests skip on non-Windows; everything else is pure unit tests.
- Solution builds cleanly with `dotnet build FocusGuard.slnx`.
- MSI builds cleanly with `dotnet build src/FocusGuard.Installer/FocusGuard.Installer.wixproj -c Release`.

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
│   ├── FocusGuard.Service/      # ✅ Worker + PipeServer + FirewallManager + DnsSinkhole + AdapterDnsManager + admin commands (steps 3–6)
│   ├── FocusGuard.Tray/         # ✅ App + tray icon + Status poll + Countdown + AdminWindow + SetPassword wizard (steps 7-8)
│   └── FocusGuard.Watchdog/     # ✅ Heartbeat-pings Service + respawns Tray (step 9)
├── tests/
│   ├── FocusGuard.Core.Tests/   # ✅ 59 tests, all green
│   └── FocusGuard.Service.Tests/ # ✅ Worker + PipeServer + FirewallManager smoke + DnsSinkhole + AdapterDnsManager + AdminCommands + PipeClientLocation + SessionLauncher + WorkerWatchdog + Tamper tests (70 total)
└── src/FocusGuard.Installer/    # ✅ WiX 5 SDK wixproj + Product.wxs (step 11)
```

`VERIFICATION.md` at the repo root holds the manual VM checklist (step 12).

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
| `Audit/IAuditLog.cs` | `IAuditLog` + `AuditCategory` enum (`AdminAction`, `StateTransition`, `AuthFailure`, `Tamper`). One line per call. |
| `Audit/FileAuditLog.cs` | Daily-rolling plaintext log: `audit-YYYY-MM-DD.log` under the service data directory. Format: `<ISO local timestamp>\t<category>\t<message>`. 30-day retention; prunes opportunistically when the date changes. Lock-protected for cross-thread appends. |
| `Security/AuthLockout.cs` | Sliding brute-force defense. Defaults: 5 consecutive failures → 30s lockout. `IsLockedOut()` clears the counter once the cooldown elapses; `RecordSuccess()` clears it immediately. Uses `IClock` for testability. |

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

5. ~~**The Tray and Watchdog projects are empty WPF/console templates.** Steps 7–9 will rewrite them.~~ *(no longer true — Tray and Watchdog are fully implemented; see gotchas 12–18 below.)*

6. **DNS sinkhole** (`DnsSinkhole.cs`) is split into a pure policy core (`HandleQueryAsync`, `SweepExpired`, `SetPosture`) and a Windows-only socket binder (`Start`/`Stop`) that hands ARSoft `QueryReceivedEventArgs` through `HandleQueryAsync`. Unit tests drive only the policy core — they never bind UDP/53. Whitelist matching is **suffix-based** (`example.com` matches `example.com` and `*.example.com`, but NOT `notexample.com`). Open posture forwards everything upstream and skips `UpsertAllowIp`. Closed posture NXDOMAINs anything not whitelisted. The Worker calls `SweepExpired` once per tick.

7. **AdapterDnsManager** captures originals **lazily** the first time `OverrideToLoopback` is called (or when a new adapter appears later). The originals are written into `FocusGuardConfig.SavedAdapterDns` so they survive a service restart. On admin Disable the Worker restores them and stops the sinkhole; on re-Enable it re-pushes loopback and restarts the sinkhole. The production WMI backend (`WmiNetworkAdapterBackend`) is exercised only by manual VM verification — unit tests use `INetworkAdapterBackend` with an in-memory fake.

8. **Upstream DNS** is configurable via `ServiceOptions.UpstreamDns` (default `["1.1.1.1", "1.0.0.1"]`). To override at install time, set `FocusGuard:UpstreamDns:0` etc. via env-var, appsettings, or `sc config` arguments — `Host.CreateApplicationBuilder` binds the section automatically.

9. **Admin commands and lockout state are in-memory.** `AuthLockout` is a Worker field — it resets to zero failures on service restart. That's deliberate (a power-cycle is far more inconvenient than waiting 30s and the lockout is just a bot-defense, not real auth) but worth knowing. The audit log persists across restarts; the lockout counter does not.

10. **`Worker.HandleSetPassword` accepts an empty old password iff no password is configured yet** (first-time setup). Once set, rotation requires the current password. `Disable` mirrors `StartBudget`'s gate and **refuses if no password is configured** — this prevents an unprovisioned install from being trivially disabled.

11. **Whitelist input is normalized** by `Worker.TryNormalizeDomain`: lowercase, single trailing dot stripped. Anything containing `/`, whitespace, or `://`, or anything without a `.`, is rejected. The whitelist stored in `FocusGuardConfig.Whitelist` is always lowercase and dot-free; `DnsSinkhole`'s suffix matcher relies on this.

12. **`PipeClient` and `PipeFraming` live in `FocusGuard.Core.Ipc`.** They were originally in `FocusGuard.Service.Ipc` but moved during step 7 so the Tray could reuse them without referencing the Service assembly. `PipeServer` stays in the Service. There is a regression test in `tests/FocusGuard.Service.Tests/PipeClientLocationTests.cs` that pins them to Core's namespace and assembly; if you "fix" them back you'll break the Tray build.

13. **Tray password-verification probe.** The Tray's `PasswordPromptDialog` sends an `AdminEndPause` request with the entered password and treats both `Success=true` and the specific error string `"not currently paused"` as "password is correct". The error wording is asserted in `AdminCommandsTests.AdminEndPause_with_correct_password_when_not_paused_returns_not_currently_paused`. If you change the wording, update the Tray + that test together.

14. **Tray runtime icon.** `FocusGuard.Tray.IconFactory` renders a coloured circle to a `Bitmap` and round-trips it through a `MemoryStream` so the resulting `Icon` owns its native data and disposing it doesn't yank the native handle out from under H.NotifyIcon. Don't optimise to `Icon.FromHandle` directly — the handle ownership semantics burn you on dispose.

15. **`SystemEvents.PowerModeChanged` for sleep/resume is NOT yet wired up.** The plan calls for it in Step 5/6 of the architecture; it's currently a known gap. Adding it lives in `Worker.cs`, not the Tray.

16. **Watchdog supervision is folded into `Worker.TickOnceAsync`.** No separate timer. Each tick `SuperviseWatchdog()` checks `_watchdogPid`: if dead and we're not Disabled and `WatchdogInterval` has elapsed since the last attempt and an interactive user exists and the watchdog exe lives at `Path.Combine(AppContext.BaseDirectory, ServiceOptions.WatchdogExeName)`, it calls `ISessionLauncher.Launch` and stores the new PID. `Worker.StopAsync` does NOT kill the watchdog — it can decide to exit on its own when the pipe ping fails repeatedly (currently it just logs).

17. **`SessionLauncher` uses `DllImport`, not `LibraryImport`.** A previous attempt with LibraryImport failed because LibraryImport's source generator emits unsafe code, which collides with `TreatWarningsAsErrors=true` unless `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is set. DllImport is safe-code-only and fully supported on .NET 10. If you ever revisit this, do NOT swap to LibraryImport without setting `<AllowUnsafeBlocks>` only on that single project AND verifying the build stays clean.

18. **Watchdog is a per-user/per-session console.** Single-instance via `Local\FocusGuard.Watchdog` mutex (the `Local\` prefix scopes the mutex to the session, which matches what the Service does — one watchdog per active console session). It pings `focusguard.cmd` over the named pipe every 5s with `GetStatus`, and respawns `FocusGuard.Tray.exe` from `AppContext.BaseDirectory` if no tray process is found by name. It does not kill itself if the pipe ping fails — the Service can survive transient stalls and we don't want the watchdog to flap.

19. **Audit emissions live in two places.** `Worker.HandleXxx` methods write `AdminAction` and `AuthFailure` lines per command. `Worker.ApplyInput` writes one `StateTransition` line per state change. `Worker.TickOnceAsync` writes one `Tamper` line per `BudgetTickEvent.ClockTamperDetected`, regardless of the current state (so the audit trail is preserved even if no session was active to end). All four `AuditCategory` values are now actually emitted.

20. **WiX 5 installer at `src/FocusGuard.Installer/`.** SDK is `WixToolset.Sdk/5.0.2` + `WixToolset.Util.wixext/5.0.2`. The wixproj sets `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` and clears `<Nullable>` etc. so the Directory.Build.props .NET-isms don't bleed into WiX. Build with `dotnet build src/FocusGuard.Installer/FocusGuard.Installer.wixproj -c Release` to produce `bin/Release/FocusGuard.msi`. The .wxs source-paths reference `$(var.FocusGuard.Service.TargetDir)FocusGuard.Service.exe` etc. — the wixproj's ProjectReferences populate these at build time. Companion files (deps.json, runtimeconfig.json, transitive .dlls) are NOT yet harvested — the .wxs only declares the three primary exes. For a real shippable MSI you need `heat dir` or `<HarvestDirectory>` to pull the publish output. Documented inline.

21. **Service-stop ACL** is set at install time via `sc.exe sdset FocusGuard "<sddl>"` in a deferred custom action in `Product.wxs`. The chosen SDDL keeps SYSTEM as full-control, lets Authenticated Users query/enumerate (so the service shows up in services.msc), lets Administrators start (RP) but **not** stop (no WP, no SERVICE_STOP). To stop the service, the password-gated `Disable` IPC command is the only path — that flows through Worker which transitions to Disabled and (intentionally) does NOT actually stop the SCM-side service.

22. **VERIFICATION.md** at the repo root is the manual VM checklist for step 12 — the human gate before shipping. Everything that can't be exercised by xUnit (real Windows Firewall, real DNS, real services.msc, real clock changes, MSI install/uninstall) lives there. Update it whenever a behavior change moves the goalposts.

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

All 12 build-sequence steps are code-complete. The next agent's job is to **execute** `VERIFICATION.md` on a clean Windows 11 VM and file follow-ups for anything that fails. The most likely follow-ups (in priority order):

1. **Implement the uninstall password guard** — currently a TODO in `src/FocusGuard.Installer/Product.wxs`. The hook point is `<Custom Action="VerifyUninstallPassword" Before="InstallValidate" Condition="REMOVE=&quot;ALL&quot; ..."/>`. Build it as a tiny .NET console exe under `src/FocusGuard.Installer/UninstallGuard/` that reads `C:\ProgramData\FocusGuard\config.dat` (DPAPI LocalMachine), shows `MessageBox.Show` for the password, calls `PasswordHasher.Verify`, and exits 0/1603.
2. **Harvest companion files in the MSI** — `Product.wxs` only declares the three primary exes. To produce a shippable MSI for a machine without the .NET 10 runtime, either `--self-contained true` the publishes or use `heat dir` to harvest `*.deps.json`, `*.runtimeconfig.json`, and the transitive DLLs. Easier path: switch the publishes to `-p:PublishSingleFile=true` and have the .wxs install one exe per project.
3. **Wire `SystemEvents.PowerModeChanged`** — gotcha 15 still applies. Sleep/resume currently does not deduct the missed time correctly.
4. **Live-fire DNS smoke test** — bind UDP/53 in a test process, query whitelisted vs. non-whitelisted. Currently only the policy core is unit-tested.
5. **Code-sign the MSI** if distributing beyond personal use.

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

The service's data directory is `C:\ProgramData\FocusGuard\` — the host creates it on first launch. EventLog source `FocusGuard` receives lifecycle entries. Plaintext audit log lives there too (`audit-YYYY-MM-DD.log`, 30-day retention).

## What is *not* yet decided (questions for the user)

- Whether the WiX installer (step 11) should be built with `WixToolset.Sdk` (modern, .NET-style csproj) or the legacy WiX 3 toolset.
- Code-signing remains out of scope per the plan's "Confirmed decisions" section.
