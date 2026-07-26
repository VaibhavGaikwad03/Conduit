using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>Executes remote system commands sent from the phone: lock and sleep.</summary>
public sealed class PowerService
{
    private readonly ILogger _log = ConduitLog.For("Power");

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
            default:
                _log.Warning("Unhandled remote command {Command}", command);
                break;
        }
    }
}
