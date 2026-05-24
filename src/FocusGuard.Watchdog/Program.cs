using System.Diagnostics;
using System.Runtime.Versioning;
using FocusGuard.Core;
using FocusGuard.Core.Ipc;

namespace FocusGuard.Watchdog;

/// <summary>
/// Per-user watchdog process. The Service launches one of these into the active console
/// session. While running it:
/// <list type="bullet">
///   <item>Heartbeat-pings the Service over its named pipe every 5 seconds.</item>
///   <item>Re-launches the Tray (<c>FocusGuard.Tray.exe</c>) into the same session if it isn't running.</item>
/// </list>
/// Single-instance via a <c>Local\</c> mutex (per session — that's exactly what we want; the
/// Service spawns one watchdog per console session it sees).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string MutexName = @"Local\FocusGuard.Watchdog";
    private const string PipeName = "focusguard.cmd";
    private const string TrayExeName = "FocusGuard.Tray.exe";
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(2);

    private static int Main()
    {
        // Single-instance guard. If another watchdog is already running in this session, exit
        // quietly — the supervisor (Service) will see *some* watchdog and stop relaunching.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Console.WriteLine("Another FocusGuard.Watchdog is already running in this session. Exiting.");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("FocusGuard.Watchdog starting.");

        try
        {
            RunLoopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        Console.WriteLine("FocusGuard.Watchdog exiting.");
        return 0;
    }

    private static async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PingServiceAsync(ct).ConfigureAwait(false);
            EnsureTrayRunning();

            try { await Task.Delay(LoopInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task PingServiceAsync(CancellationToken ct)
    {
        try
        {
            await using var client = new PipeClient(PipeName);
            await client.ConnectAsync(PipeConnectTimeout, ct).ConfigureAwait(false);
            var response = await client.SendAsync<object?>(IpcCommands.GetStatus, null, ct).ConfigureAwait(false);
            Console.WriteLine($"Heartbeat: success={response.Success} error={response.Error ?? "(none)"}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"Heartbeat failed: {ex.Message}");
        }
    }

    private static void EnsureTrayRunning()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(TrayExeName);
            var existing = Process.GetProcessesByName(name);
            try
            {
                if (existing.Length > 0)
                    return;
            }
            finally
            {
                foreach (var p in existing) p.Dispose();
            }

            var installDir = InstallLocation.Directory;
            var trayPath = Path.Combine(installDir, TrayExeName);
            if (!File.Exists(trayPath))
            {
                Console.WriteLine($"Tray exe not found at {trayPath}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = trayPath,
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var spawned = Process.Start(psi);
            Console.WriteLine($"Spawned Tray pid={spawned?.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EnsureTrayRunning failed: {ex.Message}");
        }
    }
}
