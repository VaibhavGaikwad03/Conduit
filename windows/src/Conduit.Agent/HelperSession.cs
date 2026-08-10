using System.IO.Pipes;
using Conduit.Core.Agent;
using Serilog;

namespace Conduit.Agent;

/// <summary>
/// One live capture session: launches a helper onto the target desktop, then relays its H.264 frames
/// up to the app and the app's input down to it. The agent never interprets the frames or input — it
/// just moves bytes between the two pipes, so nothing untrusted is parsed as SYSTEM.
///
/// The session is <b>desktop-aware</b>: when the console locks or unlocks the app stream stays put while
/// the helper "leg" (its process + pipe + relay thread) is torn down and relaunched onto the desktop
/// that is now on screen — the interactive Default desktop, or the secure Winlogon desktop for the lock
/// screen. The phone therefore keeps mirroring straight across the transition; only a fresh keyframe
/// arrives from the new helper.
/// </summary>
internal sealed class HelperSession : IDisposable
{
    private readonly Stream _app;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private volatile bool _running = true;

    // The current "leg": the helper on one desktop. Swapped wholesale on a retarget.
    private NamedPipeServerStream? _helperPipe;
    private IntPtr _procHandle;
    private Thread? _relay;
    private DesktopTarget _target;

    private HelperSession(Stream app, ILogger log)
    {
        _app = app;
        _log = log;
    }

    /// <summary>Starts a session, launching the first helper onto the desktop for <paramref name="initial"/>.</summary>
    public static HelperSession Launch(Stream app, DesktopTarget initial, ILogger log)
    {
        var session = new HelperSession(app, log);
        session.StartLeg(initial); // throws if the first helper can't be brought up
        return session;
    }

    /// <summary>Moves the helper to a different desktop (e.g. on lock/unlock). No-op if already there.</summary>
    public void Retarget(DesktopTarget target)
    {
        lock (_gate)
        {
            if (!_running || _target.Desktop == target.Desktop) return;
            _log.Information("Retargeting helper {From} -> {To}", _target.Desktop, target.Desktop);
            StopLeg();
            try { StartLeg(target); }
            catch (Exception ex) { _log.Error(ex, "Retarget to {Desktop} failed", target.Desktop); }
        }
    }

    // Launch a fresh helper on the target desktop and begin relaying its frames. Caller holds _gate
    // (Retarget) or is the single-threaded Launch path.
    private void StartLeg(DesktopTarget target)
    {
        var pipeName = AgentIpc.HelperPipePrefix + Guid.NewGuid().ToString("N");
        var pipe = AgentPipe.Create(pipeName, 1);
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = HelperLauncher.Launch(pipeName, target.Desktop, target.Secure, _log);
            if (!pipe.WaitForConnectionAsync().Wait(12_000))
                throw new TimeoutException("Helper did not connect");
        }
        catch
        {
            pipe.Dispose();
            HelperLauncher.Kill(handle);
            throw;
        }

        _helperPipe = pipe;
        _procHandle = handle;
        _target = target;
        _relay = new Thread(() => RelayFrames(pipe)) { IsBackground = true, Name = "agent-frame-relay" };
        _relay.Start();
        _log.Information("Helper leg live on {Desktop} (secure={Secure})", target.Desktop, target.Secure);
    }

    // Tear down the current leg. The relay thread is joined so it can't interleave frames from the old
    // helper with the next one on the shared app stream. Caller holds _gate.
    private void StopLeg()
    {
        var pipe = _helperPipe;
        var handle = _procHandle;
        var relay = _relay;
        _helperPipe = null;
        _procHandle = IntPtr.Zero;
        _relay = null;

        try { if (pipe is { IsConnected: true }) AgentFrame.Write(pipe, AgentMsg.StopCapture, ReadOnlySpan<byte>.Empty); } catch { }
        try { pipe?.Dispose(); } catch { }
        HelperLauncher.Kill(handle);
        try { relay?.Join(2_000); } catch { }
    }

    // helper → app. Runs per leg; ends when its pipe is disposed (retarget) or the helper exits.
    private void RelayFrames(NamedPipeServerStream pipe)
    {
        try
        {
            while (_running && AgentFrame.Read(pipe, out var type, out var payload))
                if (type == AgentMsg.Frame) AgentFrame.Write(_app, AgentMsg.Frame, payload);
        }
        catch (Exception ex) { if (_running) _log.Verbose(ex, "Frame relay ended"); }
    }

    // app → helper. Written to whichever leg is current; a retarget mid-input just drops that action.
    public void SendInput(byte[] inputPayload)
    {
        var pipe = _helperPipe;
        if (pipe is null) return;
        try { AgentFrame.Write(pipe, AgentMsg.Input, inputPayload); }
        catch (Exception ex) { _log.Verbose(ex, "Input relay failed"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            StopLeg();
        }
    }
}
