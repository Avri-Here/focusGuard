# FocusGuard — Windows internet self-block app

## Context

Goal: a Windows desktop app that, by default, blocks the machine from reaching the internet (every browser, Telegram, Discord, etc.). Once per 7am→7am cycle, the user gets **60 minutes of "browsing budget"**, splittable into multiple sessions, that they spend by clicking **Start my time**. When the budget runs out, the app notifies the user and re-blocks until 7am the next day.

An admin (password-protected) can:
- Pause blocking for an arbitrary duration without consuming the user's daily budget
- Maintain an "always-allowed" domain whitelist (e.g. `example.com`) reachable at any time, even during a block
- Set/change the admin password

Tamper resistance is **Medium**: a Windows Service running as `LocalSystem` plus a watchdog process; the service can't be stopped from `services.msc` without going through the app's password flow. Bypassable in Safe Mode (acceptable for a self-control app).
Recommended location: `C:\Intel\workNice\FocusGuard\` (separate repo). Stack: **.NET 8 + WPF + WiX MSI installer**.

## Decisions locked from brainstorming

| Topic | Decision |
|---|---|
| Tamper level | Medium — Windows Service + watchdog, no kernel driver |
| Block scope | Block ALL outbound internet by default; whitelist domains always reachable |
| Budget | 60 min/day, cycle = 7am→7am, **splittable** into multiple sessions |
| Whitelist mechanism | Local DNS sinkhole + dynamic Windows Firewall allow rules per resolved IP |
| Admin pause | Full open internet for chosen duration; does NOT consume daily budget |
| Password | Argon2id hash, DPAPI-encrypted config, **no recovery** (Safe Mode reset only) |
| Stack | C# / .NET 8 + WPF; WiX for MSI |

## Architecture

Three processes:

```
+--------------------------------------------------------+
|  FocusGuard.Service.exe   (Windows Service, SYSTEM)    |
|  - Owns Windows Firewall rules (block all + allow-list)|
|  - Local DNS resolver on 127.0.0.1:53                  |
|  - State machine: BLOCKED / BROWSING / PAUSED / DISABLED|
|  - Daily budget counter, persisted                     |
|  - Named-pipe IPC server (\\.\pipe\focusguard.cmd)     |
|  - Encrypted config + state (DPAPI machine scope)      |
+----------------------+---------------------------------+
                       | named pipe
+----------------------v---------------------------------+
|  FocusGuard.Tray.exe   (per-user, runs at logon)       |
|  - Tray icon + context menu                            |
|  - "Start my time" / "Pause" buttons                   |
|  - Countdown overlay window                            |
|  - Admin window (password-gated)                       |
+--------------------------------------------------------+
+--------------------------------------------------------+
|  FocusGuard.Watchdog.exe   (per-user, runs at logon)   |
|  - Re-launches Tray if killed                          |
|  - Pings Service; alerts if Service is down            |
|  - Service in turn re-launches Watchdog if killed      |
+--------------------------------------------------------+
```

## How blocking actually works

**Default firewall posture (BLOCKED state):**
1. Windows Firewall outbound rule `FG-BlockAll`: block all outbound TCP/UDP, all programs, all profiles.
2. `FG-AllowLoopback`: allow outbound to `127.0.0.0/8` (so DNS to local resolver works).
3. `FG-AllowService`: allow outbound for `FocusGuard.Service.exe` (so its DNS resolver can forward upstream queries).
4. Dynamic `FG-AllowIP-<hash>` rules: one per currently-cached whitelist IP, TTL-managed by service.

**Network adapter DNS override:** service sets every active adapter's DNS server to `127.0.0.1` via `Set-DnsClientServerAddress` / WMI. Original DNS saved so admin "Disable" can restore it.

**Local DNS resolver (UDP/TCP 53 on 127.0.0.1):**
- Whitelisted domain → forward to upstream (`1.1.1.1`), receive IP(s), upsert a `FG-AllowIP-<hash>` firewall rule with TTL = answer's DNS TTL, return IP to client.
- Non-whitelisted domain → return `NXDOMAIN`.
- During BROWSING/PAUSED state → forward all queries upstream (no allow-list filtering needed because firewall is in OPEN posture).

**OPEN posture (BROWSING or PAUSED state):**
- Disable `FG-BlockAll` (or swap it for `FG-AllowAll` — use a single named rule we toggle).
- All other rules remain in place but become moot.

**DoH bypass:** modern browsers default to DoH using fixed IPs (`1.1.1.1`, `8.8.8.8`, ...). Because our firewall blocks ALL outbound by default and only allows IPs the service has explicitly approved (via our DNS), DoH connections never reach those IPs. No browser policy push needed.

## State machine

States: `BLOCKED` (default), `BROWSING` (user session active), `PAUSED` (admin pause), `DISABLED` (admin turned off).

Daily budget:
- `cycleStartDate`: the date whose 7am started the current cycle.
- `minutesRemaining`: float, 0–60.
- At 07:00 local each day → `cycleStartDate = today`, `minutesRemaining = 60.0`. Implemented via a 60-second tick that checks whether we crossed the next 7am boundary.

Tick (1 Hz):
- If `BROWSING`: decrement `minutesRemaining` by `1/60`. If ≤ 0 → transition to `BLOCKED`, raise toast "Daily browsing time used up".
- If `PAUSED`: check `pauseEndAt`; if past → transition to `BLOCKED` (or `BROWSING` if user re-clicks Start later).
- All states: check 7am rollover.

Clock-tampering defense: persist a monotonic counter (`Environment.TickCount64` baseline + UTC anchor). If wall clock jumps backward by >5 minutes vs. monotonic, end any active session, log incident, refuse new `StartBudget` until wall clock catches up.

Sleep/hibernate: use `SystemEvents.PowerModeChanged` to pause the tick on sleep and on resume compute elapsed wall-clock to deduct missed time correctly.

## IPC

Named pipe `\\.\pipe\focusguard.cmd`. Server: service. Pipe ACL: `SYSTEM` full control, `Authenticated Users` connect+read+write. JSON request/response.

Commands the **tray (unauthenticated)** can send:
- `GetStatus` → `{ state, minutesRemaining, secondsThisSession, pauseEndAt, whitelist[], cycleResetAt }`
- `StartBudget` → starts a session if `BLOCKED` and `minutesRemaining > 0`
- `StopBudget` → ends session, returns to `BLOCKED` (preserves remaining minutes)

Commands that require a **password field** in the payload (verified server-side via Argon2id):
- `SetPassword(oldPwd, newPwd)` (oldPwd = "" only on first-time setup)
- `AddWhitelist(domain)` / `RemoveWhitelist(domain)`
- `AdminPause(durationMinutes)` / `AdminEndPause`
- `Disable` / `Enable`

The pipe is local-only; password is in plaintext over the pipe but the pipe itself is non-network. Acceptable for Medium tamper level.

## Storage

All under `C:\ProgramData\FocusGuard\` (ACL: `SYSTEM` full, `Administrators` read, `Users` no access):

- `config.dat` — DPAPI-encrypted JSON:
  ```json
  { "passwordHash": "$argon2id$...", "whitelist": ["example.com"], "adminPauseDefaultMinutes": 30 }
  ```
- `state.dat` — DPAPI-encrypted JSON, written every 5s while running and on every state transition:
  ```json
  { "cycleStartDate": "2026-05-20", "minutesRemaining": 47.3, "state": "BLOCKED", "pauseEndAt": null, "monotonicAnchor": "..." }
  ```
- `audit.log` — plaintext rolling log (last 30 days), one line per admin action / state change / tamper detection.

DPAPI scope = `LocalMachine` so the service (SYSTEM) can decrypt. The encryption is mainly to make casual file inspection harder; not crypto-grade against admin attackers.

## Tamper-resistance (Medium)

1. **Service auto-restart**: `sc failure FocusGuard reset= 0 actions= restart/5000/restart/5000/restart/5000`.
2. **Service stop ACL**: after install, `sc sdset FocusGuard` removes `SERVICE_STOP` from `BUILTIN\Administrators`, leaving only `SYSTEM`. To stop, use the app's password-gated `Disable` command, which has the service stop itself.
3. **Watchdog ↔ service mutual revival**: each pings the other every 5s. Service launches Watchdog as the logged-on interactive user via `WTSQueryUserToken` + `CreateProcessAsUser`.
4. **Tray respawn**: service ensures tray is running in the active console session.
5. **Config protection**: NTFS ACL on `C:\ProgramData\FocusGuard\` denies `Users` group all access; only readable/writable by `SYSTEM`.
6. **Uninstall guard**: WiX custom action prompts for password before allowing uninstall.
7. **Out-of-scope (acceptable bypasses)**: Safe Mode boot, booting another OS, taking the disk out. These would require a kernel driver to defeat — not in this design.

## Module / project layout

```
FocusGuard/
├── FocusGuard.sln
├── src/
│   ├── FocusGuard.Core/            # shared: models, IPC contracts, hashing, DPAPI helpers, state machine
│   ├── FocusGuard.Service/         # Windows Service host, firewall mgr, DNS resolver, IPC server
│   ├── FocusGuard.Tray/            # WPF: tray icon, countdown overlay, admin window
│   ├── FocusGuard.Watchdog/        # tiny console exe
│   └── FocusGuard.Installer/       # WiX project producing FocusGuard.msi
└── tests/
    ├── FocusGuard.Core.Tests/      # xUnit
    └── FocusGuard.Service.Tests/   # integration-ish, uses fake clock + in-memory DNS
```

Each project ≤ a few thousand lines, with clear interfaces:
- `IFirewallManager` (real impl uses `NetFwTypeLib`; fake in tests)
- `IDnsResolver`
- `IClock` (real = wall+monotonic, fake = controllable in tests)
- `IStateStore` (DPAPI-backed; in-memory in tests)
- `IIpcServer` / `IIpcClient`

Key NuGet packages:
- `Microsoft.Extensions.Hosting.WindowsServices` — service host
- `DnsClient` — upstream DNS forwarding
- `ARSoft.Tools.Net` — DNS server library for the local resolver
- `Konscious.Security.Cryptography.Argon2` — password hashing
- `H.NotifyIcon.Wpf` — modern tray icon for WPF
- `WixToolset.Sdk` — installer

## Verification plan

**Unit tests (`FocusGuard.Core.Tests`):**
- State machine: every legal/illegal transition.
- Budget tick: 60 min consumed across split sessions; sleep/resume; 7am rollover mid-session.
- Clock-tamper detection: backward jump triggers session-end + lockout.
- Argon2id verify happy path + fail path.
- DPAPI roundtrip (skipped on non-Windows CI).

**Integration tests (`FocusGuard.Service.Tests`, run as admin on Windows):**
- Install firewall rules, verify `Test-NetConnection` blocks/allows correctly per state.
- Local DNS resolver: whitelisted domain returns real IP and creates allow rule; non-whitelisted returns NXDOMAIN.
- Named-pipe IPC: status/start/admin commands; password fail → no-op + audit entry.

**Manual end-to-end on a clean Windows 11 VM:**
1. Install MSI, set initial password.
2. Verify Chrome/Edge/Telegram all fail to connect.
3. Add `wikipedia.org` to whitelist via admin window → Wikipedia loads in Chrome immediately.
4. Click **Start my time** → countdown shows 60:00 → browse freely → close browser at 35:00 → click Stop → status shows 35 min remaining → click Start again → countdown resumes from 35:00 → drain to 0 → toast appears, blocking resumes.
5. Reboot → service starts before login screen, posture is still BLOCKED with same `minutesRemaining`.
6. **Tamper checks:**
   - `services.msc` → try Stop FocusGuard → access denied.
   - Kill `FocusGuard.Tray.exe` from Task Manager → tray reappears within ~5s.
   - Kill `FocusGuard.Watchdog.exe` → reappears.
   - Change system clock to yesterday → any active session ends, audit logged.
   - Try to delete `C:\ProgramData\FocusGuard\config.dat` as standard user → access denied.
7. **Admin flows:** wrong password 5x → 30s lockout; correct password → admin window opens; "Pause 15 min" → all internet works for 15 min, daily budget unchanged; "Disable" → blocking off until "Enable".
8. 7am rollover: set clock to 06:59, observe 07:00 boundary → budget resets to 60.

## Critical files to create

(All under `FocusGuard/src/...` — paths are relative to the new repo root.)

- `FocusGuard.Core/StateMachine.cs` — pure state machine
- `FocusGuard.Core/BudgetClock.cs` — tick + rollover + tamper detect
- `FocusGuard.Core/Ipc/Contracts.cs` — request/response DTOs
- `FocusGuard.Core/Security/PasswordHasher.cs` — Argon2id wrapper
- `FocusGuard.Core/Security/DpapiStore.cs` — DPAPI-encrypted JSON file store
- `FocusGuard.Service/Program.cs` — host bootstrap
- `FocusGuard.Service/Network/FirewallManager.cs` — `INetFwPolicy2` rule CRUD
- `FocusGuard.Service/Network/DnsSinkhole.cs` — DNS server + upstream forwarder + dynamic allow-rule manager
- `FocusGuard.Service/Network/AdapterDnsManager.cs` — set/restore adapter DNS
- `FocusGuard.Service/Ipc/PipeServer.cs` — JSON-over-named-pipe
- `FocusGuard.Service/SessionLauncher.cs` — `CreateProcessAsUser` for tray/watchdog
- `FocusGuard.Tray/App.xaml` + `MainTrayIcon.xaml` + `CountdownWindow.xaml` + `AdminWindow.xaml`
- `FocusGuard.Watchdog/Program.cs`
- `FocusGuard.Installer/Product.wxs` — service install, ACL set, custom uninstall guard

## Confirmed decisions

- **Initial password setup**: first-launch wizard in the tray app. MSI does not collect a password. On first launch, before any blocking is engaged, the tray opens a one-shot setup window that requires the user to set the admin password; only after that does the service transition into `BLOCKED`.
- **Code signing**: skipped for v1. The MSI will be unsigned; users will see a SmartScreen "unrecognized publisher" prompt on install. Acceptable for personal/internal use. Revisit if distributing more widely.

## Build sequence (when implementation starts)

1. [x] Scaffold solution + 5 projects + tests. *(done 2026-05-20 — see `CLAUDE.md`)*
2. [x] `FocusGuard.Core` — state machine, password hasher, DPAPI store, IPC contracts. Full unit tests. *(done 2026-05-20 — 49 tests green)*
3. [x] `FocusGuard.Service` skeleton — Windows Service that just logs and runs IPC server. Manual install/start works. *(done 2026-05-20 — Worker hosts BudgetClock tick + StateMachine + PipeServer; NoOpFirewallManager placeholder; sc.exe install commands documented in CLAUDE.md)*
4. [x] Firewall manager — block-all + service-allow + dynamic allow rules. *(done 2026-05-20 — `FirewallManager.cs` backed by `WindowsFirewallHelper` NuGet; live smoke tests in `FocusGuard.Service.Tests/FirewallManagerTests.cs` exercise EnsureStaticRules / ApplyPosture / UpsertAllowIp+RemoveAllowIp on the real firewall, gated to elevated Windows and skipped if production rules already exist; `Test-NetConnection` verification still TODO once DNS sinkhole lands in step 5.)*
5. [x] DNS sinkhole + adapter override. Whitelist round-trip works end-to-end. *(done 2026-05-20 — `DnsSinkhole.cs` (ARSoft + DnsClient) with pure `HandleQueryAsync`/`SweepExpired`/`SetPosture` core, suffix-based whitelist match, TTL eviction. `AdapterDnsManager.cs` + `WmiNetworkAdapterBackend.cs`; originals saved into `FocusGuardConfig.SavedAdapterDns`. Worker drives posture-change → sinkhole + Disable/Enable → adapter restore/repush. Upstream is configurable via `ServiceOptions.UpstreamDns`. 14 new unit tests + 3 wiring tests, no live network needed; live-fire DNS smoke verification still TODO.)*
6. [x] State machine wired into firewall + DNS; service holds posture across restarts; password-gated admin IPC commands. *(done 2026-05-20 — posture wiring + state restoration came as part of step 5 (Worker.StartAsync re-applies posture for persisted state, sinkhole/adapters track Disable). Admin commands implemented in `Worker.cs`: SetPassword (incl. first-time setup), AddWhitelist/RemoveWhitelist (lowercase + strip-trailing-dot + reject URL-ish input), AdminPause/AdminEndPause (uses config default duration when 0), Disable (refuses without configured password) / Enable. Brute-force defense via `AuthLockout` (5 wrong tries → 30s). Plaintext rolling audit log via `FileAuditLog` at `<DataDirectory>/audit-YYYY-MM-DD.log`, 30-day retention. 21 new admin tests + 6 lockout tests + 4 audit tests, all green.)*
7. [x] `FocusGuard.Tray` — tray icon, status polling, Start/Stop, countdown overlay. *(done — H.NotifyIcon TaskbarIcon programmatically constructed in `App.xaml.cs`, runtime-generated icon via `IconFactory`, single-instance per session via `Global\FocusGuard.Tray` mutex, 2 Hz status poll, `CountdownWindow` shows MM:SS in bottom-right while Browsing, `SetPasswordWindow` first-launch wizard. `PipeClient` and `PipeFraming` moved to `FocusGuard.Core.Ipc` so the Tray can reach the Service via Core only.)*
8. [x] Admin window — password gate, whitelist edit, pause, disable. *(done — `PasswordPromptDialog` verifies the password by sending an `AdminEndPause` probe (success or "not currently paused" both indicate correct password); `AdminWindow` has tabs for Pause / Whitelist / Disable+Enable / Change password and a 1.5 s status footer poller.)*
9. [ ] `FocusGuard.Watchdog` + service-side `SessionLauncher`.
10. [ ] Tamper tests pass.
11. [ ] WiX installer — service install, ACLs, sc-failure config, sc-sdset, uninstall guard.
12. [ ] Manual VM verification per section above.

Estimated effort: ~1 week of focused work to a working v1, plus a few days hardening.

## Deviations from the original plan (recorded after step 1-2 implementation)

- **.NET version**: Plan said .NET 8; we used **.NET 10** (only SDK installed: 10.0.300). Target framework moniker is `net10.0-windows` for every project.
- **Repo location**: Plan suggested `C:\Intel\workNice\FocusGuard\`; actual code lives in `C:\Users\avrahamy\Documents\avriWorkingHere\focusGuard\` (the repo where this plan was written).
- **Solution format**: New SDK uses `FocusGuard.slnx` (XML solution), not the legacy `.sln`.
- **Firewall COM interop**: `<COMReference Include="NetFwTypeLib">` requires Visual Studio MSBuild and fails under `dotnet build`. Removed for now; the Service `.csproj` carries a comment noting this. Step 4 (FirewallManager) must re-introduce it via either a NuGet wrapper (e.g. `WindowsFirewallHelper`) or dynamic `Type.GetTypeFromProgID("HNetCfg.FwPolicy2")` interop.
