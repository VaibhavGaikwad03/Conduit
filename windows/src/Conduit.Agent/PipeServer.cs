using System.IO.Pipes;
using Conduit.Core.Agent;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.Agent;

/// <summary>
/// Hosts the app-facing named pipe and, per app connection, drives a <see cref="HelperSession"/>.
/// The pipe DACL is limited to the interactive user and SYSTEM — it is a local-only control channel,
/// never network-reachable. Only one app talks to the agent at a time (single active console user).
///
/// The server also owns the console lock state: <see cref="AgentService"/> forwards lock/unlock
/// notifications here, and this class retargets the live session's helper between the interactive and
/// secure desktops so the mirror keeps working across the lock screen.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly ILogger _log = ConduitLog.For("Agent");
    private readonly object _sessionGate = new();
    private Thread? _accept;
    private volatile bool _running;

    private HelperSession? _active;   // the one live capture session, if any
    private volatile bool _locked;    // current console lock state

    public void Start()
    {
        _running = true;
        // Assume unlocked at start and let the SCM's lock/unlock notifications correct it. (The
        // WTSINFOEX lock flag is unreliable across Windows versions, so we don't trust it here.) The
        // common case is starting a mirror while sitting at the PC, which this gets right; a mirror
        // opened while already locked is a Stage 2.1 refinement.
        _locked = false;
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "agent-accept" };
        _accept.Start();
        _log.Information("Agent pipe server started ({Pipe})", AgentIpc.AppPipeName);
    }

    /// <summary>The console just locked: move any live helper to the secure Winlogon desktop.</summary>
    public void OnDesktopLocked()
    {
        _locked = true;
        _log.Information("Console locked");
        lock (_sessionGate) _active?.Retarget(DesktopTarget.SecureDesktop);
    }

    /// <summary>The console just unlocked: move any live helper back to the interactive desktop.</summary>
    public void OnDesktopUnlocked()
    {
        _locked = false;
        _log.Information("Console unlocked");
        lock (_sessionGate) _active?.Retarget(DesktopTarget.Interactive);
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                using var app = AgentPipe.Create(AgentIpc.AppPipeName, NamedPipeServerStream.MaxAllowedServerInstances);
                app.WaitForConnection();
                _log.Information("App connected");
                HandleApp(app);
                _log.Information("App disconnected");
            }
            catch (Exception ex)
            {
                if (_running) { _log.Warning(ex, "Accept loop error"); Thread.Sleep(500); }
            }
        }
    }

    private void HandleApp(NamedPipeServerStream app)
    {
        try
        {
            while (_running && app.IsConnected && AgentFrame.Read(app, out var type, out var payload))
            {
                switch (type)
                {
                    case AgentMsg.StartCapture:
                        StartCapture(app);
                        break;

                    case AgentMsg.StopCapture:
                        StopCapture();
                        break;

                    case AgentMsg.Input:
                        HelperSession? s;
                        lock (_sessionGate) s = _active;
                        s?.SendInput(payload);
                        break;
                }
            }
        }
        catch (Exception ex) { _log.Warning(ex, "App session error"); }
        finally { StopCapture(); }
    }

    private void StartCapture(NamedPipeServerStream app)
    {
        // Target the desktop that is on screen right now, so a mirror opened while already locked comes
        // up on the secure desktop instead of showing black on Default.
        var initial = DesktopTarget.ForLockState(_locked);
        HelperSession? previous;
        lock (_sessionGate) previous = _active;
        previous?.Dispose();

        try
        {
            var session = HelperSession.Launch(app, initial, _log);
            lock (_sessionGate) _active = session;
            AgentFrame.WriteText(app, AgentMsg.Status, "capture-started");
        }
        catch (Exception ex)
        {
            lock (_sessionGate) _active = null;
            _log.Error(ex, "Failed to start capture");
            AgentFrame.WriteText(app, AgentMsg.Status, "capture-failed: " + ex.Message);
        }
    }

    private void StopCapture()
    {
        HelperSession? session;
        lock (_sessionGate) { session = _active; _active = null; }
        session?.Dispose();
    }

    public void Dispose()
    {
        _running = false;
        StopCapture();
    }
}
