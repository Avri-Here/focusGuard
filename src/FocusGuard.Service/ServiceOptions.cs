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

    public string ConfigPath => Path.Combine(DataDirectory, ConfigFileName);
    public string StatePath => Path.Combine(DataDirectory, StateFileName);
}
