# FocusGuard — Manual VM verification runbook

This runbook is the human-driven side of the plan's "Verification plan" — the parts the
test suite cannot exercise (real Windows Firewall, real DNS, real services.msc, real
clock changes). Run it on a clean **Windows 11** VM after producing an MSI from
`src/FocusGuard.Installer/`.

The expected outcome of every step is in **bold**. If any check fails, file a ticket
referencing the step number; do **not** ship.

---

## 0. Build artifacts

On the build host:

```powershell
dotnet build FocusGuard.slnx -c Release
dotnet test  FocusGuard.slnx -c Release            # all 100+ tests must pass
dotnet publish src/FocusGuard.Service/FocusGuard.Service.csproj  -c Release -r win-x64 --self-contained false -o publish/Service
dotnet publish src/FocusGuard.Tray/FocusGuard.Tray.csproj        -c Release -r win-x64 --self-contained false -o publish/Tray
dotnet publish src/FocusGuard.Watchdog/FocusGuard.Watchdog.csproj -c Release -r win-x64 --self-contained false -o publish/Watchdog
dotnet build src/FocusGuard.Installer/FocusGuard.Installer.wixproj -c Release   # produces FocusGuard.msi
```

Copy `FocusGuard.msi` to a fresh Windows 11 VM (no developer tooling installed) and
sign in as a **standard** user (Run-as-admin will be requested for the install).

---

## 1. Install + first-launch wizard

1. Double-click `FocusGuard.msi` → SmartScreen "unrecognized publisher" dialog → **More info → Run anyway**.
2. Walk through the installer (default install path: `C:\Program Files\FocusGuard\`; data dir auto-created at `C:\ProgramData\FocusGuard\`). **Expected:** install completes; the FocusGuard service shows up in `services.msc` as **Started, LocalSystem, Automatic**.
3. Within ~10 seconds the **Set admin password** wizard pops up from the tray. Enter and confirm `MyPassword!1`. **Expected:** wizard closes; FG tray icon shows *Status: BLOCKED, 60 minutes remaining*.

---

## 2. Default block posture

1. Open Edge/Chrome → navigate to `https://www.microsoft.com`. **Expected:** "Hmm, can't reach this page" / DNS error within ~5s.
2. From PowerShell: `Test-NetConnection 1.1.1.1 -Port 443`. **Expected:** TCP test fails. (DoH IPs are blocked by `FG-BlockAll`.)
3. Open Telegram Desktop / Discord (if installed). **Expected:** stays "Connecting…" forever.
4. From PowerShell: `Resolve-DnsName www.microsoft.com -Server 127.0.0.1`. **Expected:** `NXDOMAIN` (sinkhole returns NXDOMAIN for non-whitelisted in BLOCKED state).

---

## 3. Whitelist round-trip

1. Right-click tray → **Admin…** → enter `MyPassword!1`. Wrong password 5 times in a row → **Expected:** lockout message "locked out, try again in 30s". Wait 30s, enter the correct password → admin window opens.
2. Whitelist tab → add `wikipedia.org` → Add. **Expected:** appears in the list.
3. Switch to Edge → `https://www.wikipedia.org` → **Expected:** loads normally within seconds.
4. `https://www.example.com` → **Expected:** still fails (only `wikipedia.org` is whitelisted; suffix-match means `*.wikipedia.org` is reachable but `example.com` is not).
5. Whitelist tab → select `wikipedia.org` → Remove. Try Wikipedia again → **Expected:** fails again within 5–60s (depends on cached firewall rule TTL).

---

## 4. Budget cycle (split sessions)

1. Tray → **Start my time**. **Expected:** countdown overlay appears bottom-right showing `60:00`. Open Edge → any site → **Expected:** loads.
2. Wait until the overlay shows `~58:00`. Tray → **Stop**. **Expected:** overlay disappears; tray tooltip shows `BLOCKED, ~58 min remaining`. Browser → reloads stop succeeding.
3. Tray → **Start my time** again. **Expected:** countdown resumes from `~58:00`, not 60:00.
4. Optional: wait it out / set the system clock forward by 58 minutes (this counts as "tampering" — see step 7 below for tamper expectations) to drain the budget. **Expected:** when the budget hits zero, a Windows toast notifies "Daily browsing time used up" and the overlay disappears; tray returns to BLOCKED.

---

## 5. Reboot persistence

1. Reboot the VM.
2. Watch carefully: at the Windows login screen, the FG service should already have started (services.msc → FocusGuard → Status = Running on a service-host process before any user logs in).
3. Log in. **Expected:** tray icon appears within ~5s; tooltip shows the **same** `minutesRemaining` as before reboot (within ±1 minute).
4. Confirm browsing is still BLOCKED.

---

## 6. Tamper checks

These are the must-pass checks for the **Medium** tamper level.

| # | Action | Expected |
|---|---|---|
| 6a | `services.msc` → FocusGuard → right-click → **Stop**. | "Access is denied" (the service-stop ACL was set at install time so even Administrators cannot stop it). |
| 6b | `sc.exe stop FocusGuard` from an elevated PowerShell. | "[SC] OpenService FAILED 5: Access is denied." |
| 6c | Task Manager → kill `FocusGuard.Tray.exe`. | Tray icon reappears within ~10s (the Watchdog respawns it). |
| 6d | Task Manager → kill `FocusGuard.Watchdog.exe`. | Watchdog process reappears within ~15s (the Service respawns it via `SessionLauncher`). |
| 6e | As a standard user, `Get-Content C:\ProgramData\FocusGuard\config.dat`. | Access denied (NTFS ACL blocks `Users`). |
| 6f | Right-click `C:\ProgramData\FocusGuard\config.dat` → Delete. | Access denied. |
| 6g | Change the system clock backward by 1 hour while a session is active. | Active session ends immediately; tray returns to BLOCKED; a `Tamper` audit entry appears in `audit-YYYY-MM-DD.log`; further `StartBudget` requests fail until the wall clock catches up to the previously-observed monotonic anchor. |

---

## 7. Admin flows

1. **Pause:** Admin window → Pause tab → 15 minutes → Pause now. **Expected:** state goes to PAUSED; all browsers work freely; the budget counter does NOT decrease. After 15 minutes, state returns to BLOCKED automatically.
2. **End-pause early:** Pause for 30 min → "End pause" → **Expected:** immediate return to BLOCKED, budget unchanged.
3. **Disable:** Admin window → Disable. **Expected:** firewall rules become moot (browsing works); the loopback DNS override is removed (your original ISP DNS comes back in `Get-DnsClientServerAddress`). State = DISABLED. The Watchdog should NOT relaunch the Tray while disabled (Tray runs but is informational only).
4. **Enable:** Admin → Enable. **Expected:** loopback DNS pushed back onto adapters, firewall back to BLOCKED posture, budget unchanged.
5. **Change password:** Admin → Change password tab → enter old + new + confirm → Apply. **Expected:** subsequent admin operations require the new password.

---

## 8. 7am rollover

1. With `minutesRemaining` < 60, set the system clock to **06:59 local**.
2. Wait ~90 seconds. **Expected:** at 07:00 boundary the budget resets to 60.0 and `cycleStartDate` increments by one day.
3. Multi-day catch-up: stop the service, set the clock forward by 3 days, restart the service. **Expected:** budget resets to 60.0 (not 60×3 — we cap at 60); audit log notes "DailyRollover" event.

---

## 9. Uninstall

1. Settings → Apps → FocusGuard → Uninstall. **Expected:** installer prompts for the admin password before proceeding.
2. Wrong password → uninstall aborts.
3. Correct password → uninstaller stops the service, removes firewall rules `FG-*`, restores adapter DNS to the originals saved in `config.dat`, removes the install dir and `C:\ProgramData\FocusGuard\`, and removes the Run-key entries for Tray + Watchdog.
4. Reboot, confirm: no FG-related processes, no services.msc entry, original DNS in adapter properties, websites reachable normally.

---

## 10. Out-of-scope (acceptable bypasses)

These are **expected to bypass FocusGuard** and are not bugs:

- Booting Windows in Safe Mode → service does not start → user has full internet. (Mitigation would require a kernel driver — not in scope.)
- Removing the disk and reading data on another machine.
- Booting another OS off USB.

If during testing the user finds a non-Safe-Mode bypass, that **is** a bug — file it.

---

## 11. Audit log spot-check

After running steps 1–9, open `C:\ProgramData\FocusGuard\audit-YYYY-MM-DD.log` (read as
admin). **Expected entries** (each one timestamp-prefixed):

- `AdminAction\tPassword set (first time)`
- `AdminAction\tWhitelist add: wikipedia.org`
- `AdminAction\tWhitelist remove: wikipedia.org`
- `StateTransition\tBlocked -> Browsing` (and back)
- `AuthFailure\tAdminEndPause: wrong password` × 5 (from step 3)
- `Tamper\t(...)` from step 6g
- `AdminAction\tDisable` / `Enable`
- `AdminAction\tAdminPause 15min` / `AdminEndPause`
- `AdminAction\tPassword rotated`

Logs older than 30 days should be auto-pruned.

---

## Sign-off

A clean run of all sections (1–9 + 11) is the gate for shipping v1. Sign and date this
runbook in the corresponding GitHub release notes / internal Confluence.
