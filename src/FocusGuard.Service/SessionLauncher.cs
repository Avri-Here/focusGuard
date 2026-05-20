using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service;

/// <summary>
/// Launches a process **as the active console user** so that the SYSTEM-running service can
/// spawn UI processes (Tray, Watchdog) that actually attach to the interactive desktop.
/// Implementations must be safe to call when no user is logged on (return null/false).
/// </summary>
public interface ISessionLauncher
{
    /// <summary>
    /// Launch <paramref name="exePath"/> in the active console session. Returns the new
    /// process id, or <c>null</c> if no user is logged on or the launch failed. Never throws.
    /// </summary>
    int? Launch(string exePath, string? args = null);

    /// <summary>True if there is currently an active interactive console session.</summary>
    bool HasInteractiveUser();
}

/// <summary>
/// Test / non-Windows fallback. Reports no user logged on; <see cref="Launch"/> is a no-op
/// returning null.
/// </summary>
public sealed class NoOpSessionLauncher : ISessionLauncher
{
    public int? Launch(string exePath, string? args = null) => null;
    public bool HasInteractiveUser() => false;
}

/// <summary>
/// Windows-only implementation using <c>WTSGetActiveConsoleSessionId</c> /
/// <c>WTSQueryUserToken</c> / <c>CreateProcessAsUserW</c>. Errors are logged but never
/// thrown — the watchdog/tray spawn path needs to be best-effort.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionLauncher(ILogger<SessionLauncher> logger) : ISessionLauncher
{
    // ---- Win32 constants ----
    private const uint INVALID_SESSION_ID = 0xFFFFFFFF;
    private const int TOKEN_DUPLICATE = 0x0002;
    private const int TOKEN_ALL_ACCESS = 0x000F01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    private const int SecurityIdentification = 2;
    private const int TokenPrimary = 1;

    public bool HasInteractiveUser()
    {
        try
        {
            return WTSGetActiveConsoleSessionId() != INVALID_SESSION_ID;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WTSGetActiveConsoleSessionId failed");
            return false;
        }
    }

    public int? Launch(string exePath, string? args = null)
    {
        if (string.IsNullOrEmpty(exePath))
            return null;

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == INVALID_SESSION_ID)
            {
                logger.LogDebug("No active console session — skipping launch of {ExePath}", exePath);
                return null;
            }

            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                var err = Marshal.GetLastWin32Error();
                logger.LogWarning("WTSQueryUserToken failed for session {SessionId} with error {Error} ({Message})",
                    sessionId, err, new Win32Exception(err).Message);
                return null;
            }

            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>();

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, ref sa, SecurityIdentification, TokenPrimary, out primaryToken))
            {
                var err = Marshal.GetLastWin32Error();
                logger.LogWarning("DuplicateTokenEx failed with error {Error} ({Message})",
                    err, new Win32Exception(err).Message);
                return null;
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                var err = Marshal.GetLastWin32Error();
                logger.LogWarning("CreateEnvironmentBlock failed with error {Error} ({Message}) — continuing without it",
                    err, new Win32Exception(err).Message);
                environment = IntPtr.Zero;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf<STARTUPINFO>();
            si.lpDesktop = "winsta0\\default";

            // Compose command line: quoted exe + optional args.
            var commandLine = string.IsNullOrEmpty(args)
                ? "\"" + exePath + "\""
                : "\"" + exePath + "\" " + args;
            // CreateProcessAsUserW mutates the lpCommandLine buffer; pass a mutable StringBuilder.
            var cmdLineBuf = new System.Text.StringBuilder(commandLine);

            var workingDir = Path.GetDirectoryName(exePath);

            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            if (!CreateProcessAsUserW(
                    primaryToken,
                    null,
                    cmdLineBuf,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    flags,
                    environment,
                    workingDir,
                    ref si,
                    out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                logger.LogWarning("CreateProcessAsUserW failed for {ExePath} with error {Error} ({Message})",
                    exePath, err, new Win32Exception(err).Message);
                return null;
            }

            // Close the thread handle immediately; we don't need it.
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            // Closing process handle is fine — Windows keeps the process alive regardless.
            var pid = (int)pi.dwProcessId;
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

            logger.LogInformation("Launched {ExePath} as session {SessionId}, pid={Pid}", exePath, sessionId, pid);
            return pid;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected exception launching {ExePath}", exePath);
            return null;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                try { DestroyEnvironmentBlock(environment); } catch { /* best-effort */ }
            }
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ---- P/Invoke ---- (DllImport, NOT LibraryImport — keeps us out of <AllowUnsafeBlocks>.)

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingTokenHandle,
        int desiredAccess,
        ref SECURITY_ATTRIBUTES tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newTokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
