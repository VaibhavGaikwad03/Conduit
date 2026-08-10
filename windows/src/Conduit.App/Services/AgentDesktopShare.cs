using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using Conduit.Core.Agent;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// The agent-backed sibling of <see cref="DesktopShareService"/>. Instead of capturing in-process, it
/// asks the LocalSystem <c>ConduitAgent</c> service to capture the current input desktop (via a helper)
/// and relays the resulting H.264 frames to the phone; input goes back down through the agent to the
/// helper. This is the path that will keep working on the lock screen — Stage 1 proves it on the
/// ordinary desktop. Selection between this and the in-process path is a flag in FeatureCoordinator.
/// </summary>
public sealed class AgentDesktopShare : IDisposable
{
    private readonly ILogger _log = ConduitLog.For("Desktop");
    private readonly object _gate = new();

    private NamedPipeClientStream? _agent;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly byte[] _lenBuf = new byte[4];

    public bool IsRunning { get; private set; }

    /// <summary>Connects to the phone and asks the agent to start capturing the current desktop.</summary>
    public bool Start(string host, int port)
    {
        lock (_gate)
        {
            if (IsRunning) return true;
            try
            {
                _client = new TcpClient();
                _client.Connect(host, port);
                _client.NoDelay = true;
                _stream = _client.GetStream();

                _agent = new NamedPipeClientStream(".", AgentIpc.AppPipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                _agent.Connect(5_000);
                AgentFrame.Write(_agent, AgentMsg.StartCapture, ReadOnlySpan<byte>.Empty);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Could not start agent-backed desktop share (is ConduitAgent installed?)");
                Cleanup();
                return false;
            }

            IsRunning = true;
            new Thread(ReadFromAgent) { IsBackground = true, Name = "agent-desktop-read" }.Start();
            _log.Information("Agent-backed desktop mirror started -> {Host}:{Port}", host, port);
            return true;
        }
    }

    // Pull frames (and status) from the agent; forward frames length-prefixed to the phone.
    private void ReadFromAgent()
    {
        var agent = _agent;
        if (agent is null) return;
        try
        {
            while (IsRunning && AgentFrame.Read(agent, out var type, out var payload))
            {
                if (type == AgentMsg.Frame) WriteToPhone(payload);
                else if (type == AgentMsg.Status)
                    _log.Information("Agent status: {Status}", System.Text.Encoding.UTF8.GetString(payload));
            }
        }
        catch (Exception ex) { if (IsRunning) _log.Warning(ex, "Agent frame read ended"); }
        finally { if (IsRunning) StopInternal(); }
    }

    private void WriteToPhone(byte[] frame)
    {
        var stream = _stream;
        if (stream is null || frame.Length == 0) return;
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(_lenBuf, frame.Length);
            stream.Write(_lenBuf, 0, 4);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Desktop socket write failed; stopping");
            StopInternal();
        }
    }

    /// <summary>Forwards one control action to the agent (which relays it to the capturing helper).</summary>
    public void Input(InputMsg msg)
    {
        var agent = _agent;
        if (agent is null) return;
        try { AgentFrame.Write(agent, AgentMsg.Input, msg.ToBytes()); }
        catch (Exception ex) { _log.Debug(ex, "Agent input write failed ({Action})", msg.Action); }
    }

    public void Stop() => StopInternal();

    private void StopInternal()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { if (_agent is { IsConnected: true }) AgentFrame.Write(_agent, AgentMsg.StopCapture, ReadOnlySpan<byte>.Empty); } catch { }
            Cleanup();
            _log.Information("Agent-backed desktop mirror stopped");
        }
    }

    private void Cleanup()
    {
        try { _agent?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _agent = null; _stream = null; _client = null;
    }

    public void Dispose() => Stop();
}
