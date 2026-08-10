using System.ServiceProcess;
using Conduit.Core.Agent;

namespace Conduit.Agent;

/// <summary>
/// The LocalSystem Windows service host. Owns the pipe server for its lifetime and forwards the
/// console lock/unlock notifications the SCM delivers (<see cref="OnSessionChange"/>) to it, so a live
/// mirror can follow the input desktop across the lock screen.
/// </summary>
public sealed class AgentService : ServiceBase
{
    private PipeServer? _server;

    public AgentService()
    {
        ServiceName = AgentIpc.ServiceName;
        CanHandleSessionChangeEvent = true; // ask the SCM for lock/unlock notifications
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _server = new PipeServer();
        _server.Start();
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        switch (change.Reason)
        {
            case SessionChangeReason.SessionLock:
                _server?.OnDesktopLocked();
                break;
            case SessionChangeReason.SessionUnlock:
                _server?.OnDesktopUnlocked();
                break;
        }
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _server?.Dispose();
        _server = null;
    }
}
