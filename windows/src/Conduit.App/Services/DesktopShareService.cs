using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Drives the "let the phone view and control this PC" feature. When the phone asks
/// (<c>desktop-start</c>), the native capturer grabs the primary display and encodes H.264; this
/// service opens a TCP connection out to the phone (the phone is the receiver and is listening) and
/// writes each access unit length-prefixed, the same wire format the phone→PC screen mirror uses in
/// the other direction. The phone decodes it to a full-screen surface and sends back touch as
/// <c>pc-input</c>. Only one desktop share runs at a time (the native capturer is a singleton).
/// </summary>
public sealed class DesktopShareService : IDisposable
{
    /// <summary>Default port the phone listens on for the PC desktop stream (see PROTOCOL.md).</summary>
    public const int DefaultPort = 5466;

    private readonly ILogger _log = ConduitLog.For("Desktop");
    private readonly object _gate = new();
    private readonly object _writeLock = new();

    // Held in a field so the GC can't collect the thunk while native code holds the pointer.
    private ConduitDesktopNative.DesktopFrameCallback? _callback;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private byte[] _frameBuf = [];
    private readonly byte[] _lenBuf = new byte[4];

    public bool IsRunning { get; private set; }

    /// <summary>Raised when the share stops on its own (socket dropped), so the caller can reset UI state.</summary>
    public event EventHandler? Stopped;

    /// <summary>Connects to the phone and starts capturing + streaming the primary display.</summary>
    public bool Start(string host, int port)
    {
        lock (_gate)
        {
            if (IsRunning) return true;
            try
            {
                _client = new TcpClient();
                _client.Connect(host, port);   // the phone is already listening
                _client.NoDelay = true;
                _stream = _client.GetStream();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Could not reach phone {Host}:{Port} for desktop mirror", host, port);
                CleanupSocket();
                return false;
            }

            _callback = OnEncodedFrame;
            int hr = ConduitDesktopNative.ConduitDesktopStart(_callback);
            if (hr < 0)
            {
                _log.Error("ConduitDesktopStart failed: 0x{Hr:X8}", hr);
                _callback = null;
                CleanupSocket();
                return false;
            }

            IsRunning = true;
            _log.Information("Desktop mirror started -> {Host}:{Port}", host, port);
            return true;
        }
    }

    // Called on the native capture thread, once per encoded Annex-B access unit.
    private void OnEncodedFrame(IntPtr data, int len)
    {
        var stream = _stream;
        if (stream is null || len <= 0) return;
        try
        {
            if (_frameBuf.Length < len) _frameBuf = new byte[len];
            Marshal.Copy(data, _frameBuf, 0, len);
            BinaryPrimitives.WriteInt32BigEndian(_lenBuf, len);
            lock (_writeLock)
            {
                stream.Write(_lenBuf, 0, 4);
                stream.Write(_frameBuf, 0, len);
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Desktop socket write failed; stopping");
            // Stop() joins the capture thread, so it must not run *on* the capture thread.
            _ = Task.Run(() => StopInternal(notify: true));
        }
    }

    public void Stop() => StopInternal(notify: false);

    private void StopInternal(bool notify)
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            ConduitDesktopNative.ConduitDesktopStop();
            _callback = null;
            CleanupSocket();
            _log.Information("Desktop mirror stopped");
        }
        if (notify) Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupSocket()
    {
        lock (_writeLock)
        {
            try { _stream?.Dispose(); } catch { /* closing */ }
            try { _client?.Dispose(); } catch { /* closing */ }
            _stream = null;
            _client = null;
        }
    }

    public void Dispose() => Stop();
}
