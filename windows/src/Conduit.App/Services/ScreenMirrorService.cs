using System.Runtime.InteropServices;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Drives the "mirror phone screen" feature on the Windows side: opens a window, brings up the
/// native H.264 decoder, receives the phone's screen stream, and blits each decoded BGRA frame
/// into the window. The mirror image resizes itself to the phone's actual resolution.
/// </summary>
public sealed class ScreenMirrorService : IDisposable
{
    private readonly ILogger _log = ConduitLog.For("Screen");
    private readonly object _gate = new();

    private VideoStreamReceiver? _receiver;
    private ScreenMirrorWindow? _window;
    // Held in a field so the GC can't collect the thunk while native code holds the pointer.
    private ConduitScreenNative.ScreenFrameCallback? _callback;
    private byte[]? _frameBuf;

    public bool IsRunning { get; private set; }

    /// <summary>Raised when the user closes the mirror window, so the caller can tell the phone to stop.</summary>
    public event EventHandler? Closed;

    /// <summary>Opens the window and starts decoding/receiving. Returns false on failure.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning) return true;

            _callback = OnDecodedFrame;
            int hr = ConduitScreenNative.ConduitScreenFeedStart(_callback);
            if (hr < 0)
            {
                _log.Error("ConduitScreenFeedStart failed: 0x{Hr:X8}", hr);
                _callback = null;
                return false;
            }

            _window = new ScreenMirrorWindow();
            _window.Closed += (_, _) => StopInternal(fromUi: true);
            _window.Show();

            _receiver = new VideoStreamReceiver(OnEncodedFrame, VideoStreamReceiver.ScreenPort);
            _receiver.Start();

            IsRunning = true;
            _log.Information("Screen mirror started");
            return true;
        }
    }

    private void OnEncodedFrame(byte[] h264) =>
        ConduitScreenNative.ConduitScreenFeedFrame(h264, h264.Length, 0);

    // Called by native code on the receive thread once per decoded frame.
    private void OnDecodedFrame(IntPtr bgra, int width, int height, int stride)
    {
        var win = _window;
        if (win is null || !IsRunning) return;

        int size = stride * height;
        if (_frameBuf is null || _frameBuf.Length != size) _frameBuf = new byte[size];
        Marshal.Copy(bgra, _frameBuf, 0, size);
        var buf = _frameBuf;
        try
        {
            // Synchronous: WritePixels copies before we return, so reusing _frameBuf is safe.
            win.Dispatcher.Invoke(() => win.UpdateFrame(buf, width, height, stride));
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Dropped a frame during shutdown");
        }
    }

    public void Stop() => StopInternal(fromUi: false);

    private void StopInternal(bool fromUi)
    {
        ScreenMirrorWindow? toClose = null;
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;

            _receiver?.Stop();
            _receiver = null;
            ConduitScreenNative.ConduitScreenFeedStop();
            _callback = null;
            _frameBuf = null;

            toClose = fromUi ? null : _window; // if the user closed it, it's already closing
            _window = null;
            _log.Information("Screen mirror stopped");
        }

        toClose?.Dispatcher.Invoke(() => toClose.Close());
        if (fromUi) Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
