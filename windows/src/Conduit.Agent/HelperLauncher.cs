using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace Conduit.Agent;

/// <summary>
/// Launches the capture/input helper into the active console session on a chosen desktop. This is the
/// crux of the locked-PC feature and the one thing a user-session process cannot do: only a LocalSystem
/// caller (which holds SeTcbPrivilege) can query a session token and CreateProcessAsUser onto another
/// desktop.
///
/// Two desktops, two tokens:
/// <list type="bullet">
/// <item>The ordinary interactive desktop (<c>WinSta0\Default</c>) is launched under the user's own
/// elevated (linked) token so the helper is high-integrity and can drive elevated windows.</item>
/// <item>The secure desktop (<c>WinSta0\Winlogon</c> — the lock screen and UAC prompt) admits only
/// SYSTEM in its DACL, so the helper is launched under the agent's own SYSTEM token, pinned to the
/// interactive console session.</item>
/// </list>
/// </summary>
internal static class HelperLauncher
{
    /// <summary>
    /// Launches ConduitHelper.exe onto <paramref name="desktop"/>; returns the process handle.
    /// When <paramref name="secure"/> is set the helper runs as SYSTEM (for <c>WinSta0\Winlogon</c>);
    /// otherwise it runs under the interactive user's elevated token (for <c>WinSta0\Default</c>).
    /// </summary>
    public static IntPtr Launch(string pipeName, string desktop, bool secure, ILogger log)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new InvalidOperationException("No active console session");

        return secure
            ? LaunchAsSystem(pipeName, desktop, sessionId, log)
            : LaunchAsUser(pipeName, desktop, sessionId, log);
    }

    /// <summary>Interactive-desktop path: run the helper under the user's elevated linked token.</summary>
    private static IntPtr LaunchAsUser(string pipeName, string desktop, uint sessionId, ILogger log)
    {
        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed");

        // WTSQueryUserToken hands back the *filtered* (medium-integrity) token for a UAC admin, and a
        // medium-integrity SendInput is refused by elevated (high-integrity) windows — Task Manager,
        // an elevated console, an elevated Conduit. Prefer the elevated *linked* token so the helper
        // runs high-integrity and can drive those too. Falls back to the filtered token for a standard
        // user (who has no elevated linked token, and no elevated windows to inject into anyway).
        IntPtr elevatedToken = TryGetLinkedToken(userToken);
        IntPtr sourceToken = elevatedToken != IntPtr.Zero ? elevatedToken : userToken;

        IntPtr primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(sourceToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            log.Information("Helper token: {Kind}", elevatedToken != IntPtr.Zero ? "elevated (linked)" : "filtered");

            CreateEnvironmentBlock(out env, primaryToken, false); // best-effort
            return StartHelper(primaryToken, env, pipeName, desktop, sessionId, log);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (elevatedToken != IntPtr.Zero) CloseHandle(elevatedToken);
            CloseHandle(userToken);
        }
    }

    /// <summary>Secure-desktop path: run the helper as SYSTEM, pinned to the interactive console session.</summary>
    private static IntPtr LaunchAsSystem(string pipeName, string desktop, uint sessionId, ILogger log)
    {
        // The lock screen and UAC prompt live on the secure Winlogon desktop, whose DACL admits only
        // SYSTEM — a user token, even elevated, can neither open it nor inject into it. So we clone the
        // agent's own SYSTEM token and move it into the console session so the process lands on the
        // physical display rather than in the (headless) session-0 window station.
        if (!OpenProcessToken(GetCurrentProcess(),
                TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                out IntPtr selfToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

        IntPtr primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(selfToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx (system) failed");

            if (!SetTokenInformation(primaryToken, TOKEN_INFORMATION_CLASS.TokenSessionId, ref sessionId, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(session) failed");

            CreateEnvironmentBlock(out env, primaryToken, false); // best-effort
            log.Information("Helper token: SYSTEM (session {Session})", sessionId);
            return StartHelper(primaryToken, env, pipeName, desktop, sessionId, log);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(selfToken);
        }
    }

    /// <summary>Shared CreateProcessAsUser onto the target desktop; returns the process handle.</summary>
    private static IntPtr StartHelper(IntPtr primaryToken, IntPtr env, string pipeName, string desktop,
        uint sessionId, ILogger log)
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, "ConduitHelper.exe");
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("ConduitHelper.exe not found next to the agent", helperPath);
        string cmd = $"\"{helperPath}\" --pipe {pipeName}";

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
        bool ok = CreateProcessAsUser(
            primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
            env, Path.GetDirectoryName(helperPath), ref si, out PROCESS_INFORMATION pi);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");

        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        log.Information("Launched helper pid {Pid} on {Desktop} (session {Session})",
            pi.dwProcessId, desktop, sessionId);
        return pi.hProcess;
    }

    /// <summary>Returns the elevated linked token for a UAC-filtered admin token, or Zero if none.</summary>
    private static IntPtr TryGetLinkedToken(IntPtr token)
    {
        int size = Marshal.SizeOf<TOKEN_LINKED_TOKEN>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken, buf, size, out _))
                return Marshal.PtrToStructure<TOKEN_LINKED_TOKEN>(buf).LinkedToken;
            return IntPtr.Zero; // standard user or already-elevated token: no linked token
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static void Kill(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero) return;
        try { TerminateProcess(processHandle, 0); } catch { /* already gone */ }
        CloseHandle(processHandle);
    }

    // ---- Win32 ----

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }
    private enum TOKEN_INFORMATION_CLASS { TokenSessionId = 12, TokenLinkedToken = 19 }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_LINKED_TOKEN { public IntPtr LinkedToken; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, IntPtr attrs,
        SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS infoClass,
        IntPtr info, int infoLen, out int returnLen);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS infoClass,
        ref uint info, int infoLen);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? appName, string? commandLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDir,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
