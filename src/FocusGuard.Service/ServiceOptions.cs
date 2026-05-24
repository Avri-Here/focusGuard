using FocusGuard.Core;

namespace FocusGuard.Service;

/// <summary>
/// Runtime configuration for the FocusGuard service host. Defaults match the
/// architecture document (data under <c>C:\ProgramData\FocusGuard</c>, IPC pipe
/// at <c>focusguard.cmd</c>); tests override these to use a temp directory and
/// a uniquely-named pipe.
/// </summary>
public sealed class ServiceOptions
{
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusGuard");

    public string PipeName { get; set; } = "focusguard.cmd";

    public string ConfigFileName { get; set; } = "config.dat";
    public string StateFileName { get; set; } = "state.dat";

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan StatePersistInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upstream DNS server(s) the sinkhole forwards whitelisted queries to. Default: Cloudflare's
    /// 1.1.1.1 / 1.0.0.1. Override via <c>FocusGuard:UpstreamDns:0</c> etc. in configuration.
    /// </summary>
    public string[] UpstreamDns { get; set; } = new[] { "1.1.1.1", "1.0.0.1" };

    public int UpstreamDnsPort { get; set; } = 53;

    /// <summary>Filename of the per-user watchdog launched by the service into the active console session.</summary>
    public string WatchdogExeName { get; set; } = "FocusGuard.Watchdog.exe";

    /// <summary>
    /// Directory containing the installed FocusGuard executables. Defaults to the directory
    /// of the running service exe via <see cref="InstallLocation.Directory"/> — that's correct
    /// in production, including under <c>PublishSingleFile</c> where
    /// <see cref="AppContext.BaseDirectory"/> would point at a per-exe self-extract folder
    /// instead of the install dir. Tests override this to point at the test bin folder.
    /// </summary>
    public string? InstallDirectory { get; set; }

    /// <summary>Resolved install directory: <see cref="InstallDirectory"/> or the live process path's dir.</summary>
    public string ResolveInstallDirectory() => InstallDirectory ?? InstallLocation.Directory;

    /// <summary>
    /// Minimum gap between watchdog launch attempts. Prevents busy-loop relaunching when the
    /// watchdog can't start (e.g. user not yet logged on, exe missing).
    /// </summary>
    public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(10);

    public string ConfigPath => Path.Combine(DataDirectory, ConfigFileName);
    public string StatePath => Path.Combine(DataDirectory, StateFileName);
}
