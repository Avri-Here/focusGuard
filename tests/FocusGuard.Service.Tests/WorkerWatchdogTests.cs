using FocusGuard.Core;
using FocusGuard.Core.Ipc;
using FocusGuard.Service;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Tests that the <see cref="Worker"/> drives <see cref="ISessionLauncher"/> per its watchdog
/// supervision contract: launch when state != Disabled and no live PID, throttle by
/// <see cref="ServiceOptions.WatchdogInterval"/>, no-op when no interactive user, no-op when
/// Disabled.
/// </summary>
public class WorkerWatchdogTests
{
    /// <summary>
    /// We need <c>File.Exists(Path.Combine(InstallDir, WatchdogExeName))</c> to be true so
    /// Worker actually attempts a launch. The test bin folder is <see cref="AppContext.BaseDirectory"/>
    /// — pin <see cref="ServiceOptions.InstallDirectory"/> to it and pick any file living there
    /// as the "watchdog" filename.
    /// </summary>
    private static string TestInstallDir() => AppContext.BaseDirectory;

    private static string ExistingExeInBaseDir()
    {
        var here = TestInstallDir();
        return Path.GetFileName(Directory.EnumerateFiles(here, "*.dll").First())!;
    }

    /// <summary>
    /// A PID that is overwhelmingly unlikely to belong to a live process.
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/> throws for these and the
    /// Worker treats that as "watchdog is dead" → relaunch.
    /// </summary>
    private static int UnusedPid() => 0x7FFF_FFFE;

    private static ServiceOptions BuildOptions(TimeSpan? watchdogInterval = null) => new()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "FocusGuardTests-" + Guid.NewGuid().ToString("N")),
        PipeName = "focusguard.test." + Guid.NewGuid().ToString("N"),
        InstallDirectory = TestInstallDir(),
        WatchdogExeName = ExistingExeInBaseDir(),
        WatchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task Tick_launches_watchdog_when_state_is_blocked_and_no_live_pid()
    {
        var fakeLauncher = new FakeSessionLauncher { HasUser = true, NextPid = UnusedPid() };
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            sessionLauncher: fakeLauncher,
            optionsOverride: BuildOptions());
        await harness.StartAsync();

        await harness.Worker.TickForTestsAsync();

        Assert.True(fakeLauncher.LaunchCalls >= 1);
    }

    [Fact]
    public async Task Tick_does_not_launch_when_state_is_disabled()
    {
        var fakeLauncher = new FakeSessionLauncher { HasUser = true, NextPid = UnusedPid() };
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            seedState: new FocusGuardState
            {
                CycleStartDate = new DateOnly(2026, 5, 20),
                MinutesRemaining = 30,
                State = FocusState.Disabled,
            },
            sessionLauncher: fakeLauncher,
            optionsOverride: BuildOptions());
        await harness.StartAsync();

        await harness.Worker.TickForTestsAsync();

        Assert.Equal(0, fakeLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Tick_does_not_launch_when_no_interactive_user()
    {
        var fakeLauncher = new FakeSessionLauncher { HasUser = false, NextPid = UnusedPid() };
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            sessionLauncher: fakeLauncher,
            optionsOverride: BuildOptions());
        await harness.StartAsync();

        await harness.Worker.TickForTestsAsync();

        Assert.Equal(0, fakeLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Tick_throttles_relaunch_until_WatchdogInterval_elapses()
    {
        var fakeLauncher = new FakeSessionLauncher { HasUser = true, NextPid = UnusedPid() };
        var harness = WorkerHarness.Build(
            seedConfig: new FocusGuardConfig { PasswordHash = "$argon2id$..." },
            sessionLauncher: fakeLauncher,
            optionsOverride: BuildOptions(TimeSpan.FromSeconds(10)));
        await harness.StartAsync();

        await harness.Worker.TickForTestsAsync();
        var afterFirst = fakeLauncher.LaunchCalls;
        Assert.True(afterFirst >= 1);

        // A second tick within the throttle window must NOT trigger another launch even
        // though the previous PID points at a dead process.
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Worker.TickForTestsAsync();
        Assert.Equal(afterFirst, fakeLauncher.LaunchCalls);

        // Now advance beyond the interval; the next tick relaunches.
        harness.Clock.Advance(TimeSpan.FromSeconds(15));
        await harness.Worker.TickForTestsAsync();
        Assert.True(fakeLauncher.LaunchCalls > afterFirst);
    }
}

/// <summary>Simple recording fake. PID returned is configurable so tests can simulate dead processes.</summary>
internal sealed class FakeSessionLauncher : ISessionLauncher
{
    public bool HasUser { get; set; } = true;
    public int? NextPid { get; set; }
    public int LaunchCalls { get; private set; }
    public List<string> LaunchedPaths { get; } = new();

    public bool HasInteractiveUser() => HasUser;

    public int? Launch(string exePath, string? args = null)
    {
        LaunchCalls++;
        LaunchedPaths.Add(exePath);
        return NextPid;
    }
}

