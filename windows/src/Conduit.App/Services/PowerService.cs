using System.Diagnostics;
using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Executes remote system commands sent from the phone: lock, sleep, shut down, and "find my
/// PC" (an audible alert that also brings the window to the front). (Volume is handled by the
/// media remote via <see cref="MediaService"/>.)
/// </summary>
public sealed class PowerService
{
    private readonly ILogger _log = ConduitLog.For("Power");

    /// <summary>Raised on "find my PC" so the UI can surface the window. Wired up in App.</summary>
    public event Action? FindMyPcRequested;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public void Execute(string command)
    {
        _log.Information("Remote command: {Command}", command);
        switch (command)
        {
            case "lock":
                if (!LockWorkStation())
                    _log.Warning("LockWorkStation failed: {Err}", Marshal.GetLastWin32Error());
                break;
            case "sleep":
                if (!SetSuspendState(false, false, false))
                    _log.Warning("SetSuspendState failed: {Err}", Marshal.GetLastWin32Error());
                break;
            case "shutdown":
                // Graceful shutdown; the /t 0 makes it immediate. User-initiated from their phone.
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case "findpc":
                FindMyPc();
                break;
            default:
                _log.Warning("Unhandled remote command {Command}", command);
                break;
        }
    }

    // Beep a few times so the user can locate the PC, and ask the UI to pop to the front.
    private void FindMyPc()
    {
        FindMyPcRequested?.Invoke();
        Task.Run(() =>
        {
            for (var i = 0; i < 6; i++)
            {
                System.Media.SystemSounds.Exclamation.Play();
                Thread.Sleep(500);
            }
        });
    }
}
