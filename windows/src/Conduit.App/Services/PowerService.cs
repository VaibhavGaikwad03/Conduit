using System.Diagnostics;
using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Executes remote system commands sent from the phone: lock, sleep, shut down, adjust the
/// PC volume, and "find my PC" (an audible alert that also brings the window to the front).
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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
            case "volup":
                TapKey(VK_VOLUME_UP);
                break;
            case "voldown":
                TapKey(VK_VOLUME_DOWN);
                break;
            case "mute":
                TapKey(VK_VOLUME_MUTE);
                break;
            case "findpc":
                FindMyPc();
                break;
            default:
                _log.Warning("Unhandled remote command {Command}", command);
                break;
        }
    }

    private static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
