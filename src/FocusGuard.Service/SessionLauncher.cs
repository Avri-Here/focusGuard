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
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private const int SecurityIdentification = 2;
    private const int TokenPrimary = 1;

    public bool HasInteractiveUser() => TryFindInteractiveSession(out _);

    public int? Launch(string exePath, string? args = null)
    {
        if (string.IsNullOrEmpty(exePath))
            return null;

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;

        try
        {
            if (!TryFindInteractiveSession(out var sessionId))
            {
                logger.LogDebug("No active interactive session — skipping launch of {ExePath}", exePath);
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

            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

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

    /// <summary>
    /// Find a session we can launch a UI process into. Strategy:
    /// 1. Try <c>WTSGetActiveConsoleSessionId</c> — fast path for the local console user.
    ///    Skip session 0 (Services session — not interactive) and INVALID_SESSION_ID.
    /// 2. Fall back to <c>WTSEnumerateSessions</c> and pick the first <c>WTSActive</c> session
    ///    that has a real user token (so an unattended Windows Hello / login screen does
    ///    not match). This handles RDP sessions and the brief window after Windows boot
    ///    when the console session id is not yet set.
    /// </summary>
    private bool TryFindInteractiveSession(out uint sessionId)
    {
        try
        {
            var consoleId = WTSGetActiveConsoleSessionId();
            if (consoleId != INVALID_SESSION_ID && consoleId != 0 && SessionHasUserToken(consoleId))
            {
                sessionId = consoleId;
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WTSGetActiveConsoleSessionId failed — falling through to enumerate");
        }

        sessionId = 0;
        var pSessions = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out pSessions, out var count))
            {
                var err = Marshal.GetLastWin32Error();
                logger.LogDebug("WTSEnumerateSessionsW failed with error {Error}", err);
                return false;
            }

            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var ptr = IntPtr.Add(pSessions, i * size);
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);
                if (info.State != WTS_CONNECTSTATE_CLASS.WTSActive) continue;
                if (info.SessionID == 0) continue; // Services session
                if (!SessionHasUserToken(info.SessionID)) continue;
                sessionId = info.SessionID;
                return true;
            }
            return false;
        }
        finally
        {
            if (pSessions != IntPtr.Zero)
            {
                try { WTSFreeMemory(pSessions); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Probe a session by trying to query its user token. Returns true iff a token is
    /// available — i.e. there's a real user signed in and we can spawn UI as them.
    /// Caller does NOT receive the token; we close it immediately so the real Launch
    /// path can re-query without lifetime entanglement.
    /// </summary>
    private static bool SessionHasUserToken(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token)) return false;
        if (token != IntPtr.Zero) CloseHandle(token);
        return true;
    }

    // ---- P/Invoke ---- (DllImport, NOT LibraryImport — keeps us out of <AllowUnsafeBlocks>.)

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer,
        uint reserved,
        uint version,
        out IntPtr ppSessionInfo,
        out uint count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionID;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

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
