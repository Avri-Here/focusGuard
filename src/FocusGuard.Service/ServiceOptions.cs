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

    public string ConfigPath => Path.Combine(DataDirectory, ConfigFileName);
    public string StatePath => Path.Combine(DataDirectory, StateFileName);
}
