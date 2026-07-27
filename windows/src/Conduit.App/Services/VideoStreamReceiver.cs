using System.IO;
using System.Net;
using System.Net.Sockets;
using Conduit.Core.Logging;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Accepts the phone's dedicated video connection and reads length-prefixed H.264
/// access units off it. Video is far too heavy for the JSON session channel, so it
/// gets its own TCP port. Wire format per frame: a 4-byte big-endian length followed
/// by that many bytes of Annex-B H.264. Each frame is handed to <c>onFrame</c>.
/// </summary>
public sealed class VideoStreamReceiver
{
    /// <summary>Dedicated video port (session is 5462, discovery 5461).</summary>
    public const int Port = 5463;

    private readonly ILogger _log = ConduitLog.For("Webcam");
    private readonly Action<byte[]> _onFrame;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public VideoStreamReceiver(Action<byte[]> onFrame) => _onFrame = onFrame;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _log.Information("Video receiver listening on {Port}", Port);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                _log.Information("Phone video stream connected");
                await ReadFrames(client.GetStream(), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.Warning(ex, "Video stream dropped; awaiting reconnect"); }
        }
    }

    private async Task ReadFrames(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            await ReadExact(stream, lenBuf, 4, ct);
            int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len <= 0 || len > 8 * 1024 * 1024) throw new InvalidDataException($"Bad frame length {len}");

            var frame = new byte[len];
            await ReadExact(stream, frame, len, ct);
            _onFrame(frame);
        }
    }

    private static async Task ReadExact(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException("Video stream closed");
            read += n;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }
}
