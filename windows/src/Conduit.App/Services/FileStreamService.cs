using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Conduit.Core.Networking;
using Conduit.Core.Protocol;
using Conduit.Core.Security;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// The fast path for large files. Instead of base64 chunks over the JSON session, the bytes stream
/// raw over a dedicated TCP port (<see cref="ConduitPorts.FileStream"/>) in big AES-256-GCM blocks —
/// no base64, no per-chunk JSON, far fewer allocations. The sender still announces the transfer with
/// a <c>file-offer</c> (with <c>stream:true</c>) over the encrypted session; the raw stream is bound
/// to that offer by transferId and encrypted with the same per-peer key, so it stays end-to-end
/// secure and a stranger can't inject bytes.
/// </summary>
public sealed class FileStreamService
{
    private const int BlockSize = 1 * 1024 * 1024;         // 1 MB plaintext per encrypted block
    private static readonly byte[] Magic = "CFS1"u8.ToArray(); // Conduit File Stream v1

    private readonly ILogger _log = ConduitLog.For("FileStream");
    private readonly ConduitNode _node;
    private readonly string _downloadFolder;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // Offers we've been told to expect (via file-offer{stream}) but whose stream hasn't arrived yet.
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public event EventHandler<TransferProgress>? Progress;
    public event EventHandler<string>? FileReceived;

    private sealed record Pending(string SenderDeviceId, string Name, long Size);

    public FileStreamService(ConduitNode node, string downloadFolder)
    {
        _node = node;
        _downloadFolder = downloadFolder;
        Directory.CreateDirectory(downloadFolder);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, ConduitPorts.FileStream);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Information("File stream listener started on {Port}", ConduitPorts.FileStream);
    }

    /// <summary>A peer announced (via file-offer{stream}) that it's about to stream this file to us.</summary>
    public void RegisterIncoming(string transferId, string senderDeviceId, string name, long size)
    {
        if (string.IsNullOrEmpty(transferId)) return;
        var clean = Path.GetFileName(name);
        _pending[transferId] = new Pending(senderDeviceId, clean, size);
        Report(new TransferProgress { Id = transferId, Name = clean, IsSending = false, Total = size });
        // If the raw stream never connects, don't leave the UI stuck at 0%.
        _ = Task.Delay(20_000).ContinueWith(t =>
        {
            if (_pending.TryRemove(transferId, out _))
            {
                _log.Warning("Stream for {Name} never arrived", clean);
                Report(new TransferProgress { Id = transferId, Name = clean, IsSending = false, Total = size, Done = true, Failed = true });
            }
        });
    }

    // ---- Sending --------------------------------------------------------------

    /// <summary>Announces the file, then streams its bytes raw+encrypted to the peer's stream port.</summary>
    public async Task SendFileAsync(string deviceId, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { _log.Warning("File not found: {Path}", path); return; }

        var key = _node.SessionKeyFor(deviceId);
        var ip = _node.IpFor(deviceId);
        if (key is null || ip is null)
        {
            _log.Warning("No key/ip for {Id}; can't stream {Name}", deviceId, info.Name);
            return;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var progress = new TransferProgress { Id = transferId, Name = info.Name, IsSending = true, Total = info.Length };
        Report(progress);
        _log.Information("Streaming {Name} ({Size} bytes) to {Id}", info.Name, info.Length, deviceId);

        await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileOffer, b =>
        {
            b["transferId"] = transferId;
            b["name"] = info.Name;
            b["size"] = info.Length;
            b["mime"] = "application/octet-stream";
            b["stream"] = true;
        }));

        try
        {
            using var client = new TcpClient { SendBufferSize = 1 << 20 };
            await client.ConnectAsync(ip, ConduitPorts.FileStream);
            await using var net = client.GetStream();

            // Header: magic, our device id, the transfer id — all plaintext, just to route the stream.
            await FrameCodec.WriteFrameAsync(net, Magic, default);
            await FrameCodec.WriteFrameAsync(net, Encoding.UTF8.GetBytes(_node.Self.DeviceId), default);
            await FrameCodec.WriteFrameAsync(net, Encoding.UTF8.GetBytes(transferId), default);

            var cipher = new SessionCipher(key);
            var buffer = new byte[BlockSize];
            long sent = 0;
            int lastPercent = -1;
            await using var fs = File.OpenRead(path);
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                var plain = read == buffer.Length ? buffer : buffer[..read];
                await FrameCodec.WriteFrameAsync(net, cipher.Encrypt(plain), default);
                sent += read;
                progress.Transferred = sent;
                if (progress.Percent != lastPercent) { lastPercent = progress.Percent; Report(progress); }
            }
            await FrameCodec.WriteFrameAsync(net, [], default); // zero-length frame = end of stream

            progress.Transferred = progress.Total;
            progress.Done = true;
            Report(progress);
            _log.Information("Finished streaming {Name}", info.Name);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed streaming {Name}", info.Name);
            progress.Failed = true;
            Report(progress);
        }
    }

    // ---- Receiving ------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleIncomingAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.Warning(ex, "File stream accept error"); }
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        using var clientScope = client;
        client.ReceiveBufferSize = 1 << 20;
        string? dest = null;
        FileStream? fs = null;
        try
        {
            await using var net = client.GetStream();
            var magic = await FrameCodec.ReadFrameAsync(net, ct);
            if (magic is null || !magic.AsSpan().SequenceEqual(Magic)) return;
            var senderId = Encoding.UTF8.GetString(await FrameCodec.ReadFrameAsync(net, ct) ?? []);
            var transferId = Encoding.UTF8.GetString(await FrameCodec.ReadFrameAsync(net, ct) ?? []);

            var pending = await WaitForOfferAsync(transferId, ct);
            if (pending is null) { _log.Warning("Stream for unknown transfer {Id} — dropping", transferId); return; }
            _pending.TryRemove(transferId, out _);

            var key = _node.SessionKeyFor(senderId);
            if (key is null) { _log.Warning("No key for stream sender {Id}", senderId); return; }
            var cipher = new SessionCipher(key);

            dest = UniquePath(Path.Combine(_downloadFolder, pending.Name));
            fs = File.Create(dest);
            _log.Information("Receiving stream {Name} ({Size} bytes) → {Dest}", pending.Name, pending.Size, dest);

            var progress = new TransferProgress { Id = transferId, Name = pending.Name, IsSending = false, Total = pending.Size };
            long received = 0;
            int lastPercent = -1;
            while (true)
            {
                var frame = await FrameCodec.ReadFrameAsync(net, ct);
                if (frame is null || frame.Length == 0) break; // clean close or end marker
                byte[] plain = cipher.Decrypt(frame);
                await fs.WriteAsync(plain, ct);
                received += plain.Length;
                progress.Transferred = received;
                if (progress.Percent != lastPercent) { lastPercent = progress.Percent; Report(progress); }
            }
            await fs.FlushAsync(ct);
            fs.Dispose();
            fs = null;

            bool ok = pending.Size <= 0 || received == pending.Size;
            _log.Information("Stream {Name} {Result} ({Recv}/{Size})", pending.Name, ok ? "complete" : "truncated", received, pending.Size);
            Report(new TransferProgress
            {
                Id = transferId, Name = pending.Name, IsSending = false,
                Transferred = received, Total = pending.Size, Done = true, Failed = !ok
            });
            if (ok) FileReceived?.Invoke(this, dest);
            else TryDelete(dest);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "File stream receive failed");
            try { fs?.Dispose(); } catch { /* ignore */ }
            if (dest is not null) TryDelete(dest);
        }
    }

    /// <summary>Waits briefly for the matching file-offer to arrive over the session (it may race the stream).</summary>
    private async Task<Pending?> WaitForOfferAsync(string transferId, CancellationToken ct)
    {
        for (int i = 0; i < 50; i++) // up to ~5s
        {
            if (_pending.TryGetValue(transferId, out var p)) return p;
            try { await Task.Delay(100, ct); } catch { return null; }
        }
        return null;
    }

    private void Report(TransferProgress p) => Progress?.Invoke(this, p);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
    }
}
