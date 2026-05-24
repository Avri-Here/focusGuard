namespace FocusGuard.Core;

/// <summary>
/// Resolves the directory that contains the running exe on disk.
///
/// Unlike <see cref="AppContext.BaseDirectory"/>, this is correct under
/// single-file self-extracting publishes (<c>PublishSingleFile=true</c> +
/// <c>IncludeAllContentForSelfExtract=true</c>): in that mode the runtime
/// extracts payload to <c>%TEMP%\.net\&lt;exe&gt;\&lt;hash&gt;\</c> and
/// <see cref="AppContext.BaseDirectory"/> points at that *extract* folder,
/// not at the exe's actual install directory. Each app gets its own extract
/// folder, so siblings cannot find each other through BaseDirectory.
///
/// <see cref="Environment.ProcessPath"/> always returns the real on-disk
/// path of the launching exe, which is what we want when one FocusGuard
/// component (Service, Watchdog) needs to locate another (Watchdog, Tray)
/// installed alongside it under <c>%ProgramFiles%\FocusGuard\</c>.
/// </summary>
public static class InstallLocation
{
    /// <summary>
    /// The directory that contains the running exe on disk. Throws
    /// <see cref="InvalidOperationException"/> if the host can't report its
    /// own process path (should never happen on .NET 6+).
    /// </summary>
    public static string Directory =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? throw new InvalidOperationException(
            "Environment.ProcessPath is null; cannot resolve the FocusGuard install directory.");
}
