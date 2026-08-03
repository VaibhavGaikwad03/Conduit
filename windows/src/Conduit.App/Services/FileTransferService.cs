using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Conduit.Core.Logging;
using Conduit.Core.Networking;
using Conduit.Core.Protocol;
using Serilog;

namespace Conduit.App.Services;

/// <summary>A live snapshot of one file transfer, raised as it progresses.</summary>
public sealed class TransferProgress
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsSending { get; init; }
    public long Transferred { get; set; }
    public long Total { get; set; }
    public bool Done { get; set; }
    public bool Failed { get; set; }
    public int Percent => Total > 0 ? (int)Math.Clamp(Transferred * 100 / Total, 0, 100) : (Done ? 100 : 0);
}

/// <summary>
/// Sends and receives files over the session as a series of base64 chunks
/// (file-offer → file-chunk* → file-complete). Reports progress via <see cref="Progress"/>.
/// </summary>
public sealed class FileTransferService
{
    private const int ChunkSize = 64 * 1024; // 64 KB of raw bytes per chunk

    private readonly ILogger _log = ConduitLog.For("FileTransfer");
    private readonly ConduitNode _node;
    private readonly string _downloadFolder;
    private readonly ConcurrentDictionary<string, Incoming> _incoming = new();

    public event EventHandler<string>? FileReceived;       // full path
    public event EventHandler<TransferProgress>? Progress;  // live progress updates

    public FileTransferService(ConduitNode node, string downloadFolder)
    {
        _node = node;
        _downloadFolder = downloadFolder;
        Directory.CreateDirectory(_downloadFolder);
    }

    private sealed class Incoming
    {
        public required string TransferId;
        public required string Name;
        public required string Path;
        public required FileStream Stream;
        public required long Size;
        public long Received;
        public int NextSeq;
        public int LastPercent = -1;
        public readonly SortedDictionary<int, byte[]> Pending = new();
    }

    // ---- Sending --------------------------------------------------------------

    public async Task SendFileAsync(string deviceId, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { _log.Warning("File not found: {Path}", path); return; }

        string transferId = Guid.NewGuid().ToString("N");
        _log.Information("Sending {Name} ({Size} bytes) to {Id}", info.Name, info.Length, deviceId);

        var progress = new TransferProgress
        {
            Id = transferId, Name = info.Name, IsSending = true, Total = info.Length
        };
        Report(progress);

        try
        {
            await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileOffer, b =>
            {
                b["transferId"] = transferId;
                b["name"] = info.Name;
                b["size"] = info.Length;
                b["mime"] = "application/octet-stream";
            }));

            using var sha = SHA256.Create();
            await using var fs = File.OpenRead(path);
            var buffer = new byte[ChunkSize];
            int seq = 0, read, lastPercent = -1;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                string b64 = Convert.ToBase64String(buffer, 0, read);
                int thisSeq = seq++;
                await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileChunk, b =>
                {
                    b["transferId"] = transferId;
                    b["seq"] = thisSeq;
                    b["dataB64"] = b64;
                }));

                progress.Transferred += read;
                if (progress.Percent != lastPercent)
                {
                    lastPercent = progress.Percent;
                    Report(progress);
                }
            }
            sha.TransformFinalBlock([], 0, 0);
            string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileComplete, b =>
            {
                b["transferId"] = transferId;
                b["ok"] = true;
                b["sha256"] = hash;
            }));
            progress.Transferred = progress.Total;
            progress.Done = true;
            Report(progress);
            _log.Information("Finished sending {Name}", info.Name);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed sending {Name}", info.Name);
            progress.Failed = true;
            Report(progress);
            // Tell the receiver we failed (e.g. a locked/unreadable file) so it doesn't hang at 0%.
            try
            {
                await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileComplete, b =>
                {
                    b["transferId"] = transferId;
                    b["ok"] = false;
                }));
            }
            catch { /* peer may be gone; nothing more we can do */ }
        }
    }

    // ---- Receiving ------------------------------------------------------------

    public void HandlePacket(Packet packet)
    {
        switch (packet.Type)
        {
            case PacketType.FileOffer: BeginReceive(packet); break;
            case PacketType.FileChunk: ReceiveChunk(packet); break;
            case PacketType.FileComplete: CompleteReceive(packet); break;
        }
    }

    private void BeginReceive(Packet packet)
    {
        string transferId = packet.GetString("transferId") ?? "";
        string name = Path.GetFileName(packet.GetString("name") ?? "conduit-file");
        long size = packet.GetLong("size");
        string dest = UniquePath(Path.Combine(_downloadFolder, name));

        var incoming = new Incoming
        {
            TransferId = transferId,
            Name = name,
            Path = dest,
            Stream = File.Create(dest),
            Size = size
        };
        _incoming[transferId] = incoming;
        _log.Information("Receiving {Name} ({Size} bytes) → {Dest}", name, size, dest);
        Report(new TransferProgress { Id = transferId, Name = name, IsSending = false, Total = size });
    }

    private void ReceiveChunk(Packet packet)
    {
        string transferId = packet.GetString("transferId") ?? "";
        if (!_incoming.TryGetValue(transferId, out var inc)) return;

        int seq = packet.GetInt("seq");
        byte[] data = Convert.FromBase64String(packet.GetString("dataB64") ?? "");
        inc.Pending[seq] = data;

        // Flush any in-order chunks to disk to keep memory bounded.
        while (inc.Pending.TryGetValue(inc.NextSeq, out var chunk))
        {
            inc.Stream.Write(chunk, 0, chunk.Length);
            inc.Received += chunk.Length;
            inc.Pending.Remove(inc.NextSeq);
            inc.NextSeq++;
        }

        var progress = new TransferProgress
        {
            Id = inc.TransferId, Name = inc.Name, IsSending = false,
            Transferred = inc.Received, Total = inc.Size
        };
        if (progress.Percent != inc.LastPercent)
        {
            inc.LastPercent = progress.Percent;
            Report(progress);
        }
    }

    private void CompleteReceive(Packet packet)
    {
        string transferId = packet.GetString("transferId") ?? "";
        if (!_incoming.TryRemove(transferId, out var inc)) return;

        // The sender couldn't read the file (e.g. a locked file) — clean up and fail, don't hang at 0%.
        if (!packet.GetBool("ok", true))
        {
            inc.Stream.Dispose();
            try { File.Delete(inc.Path); } catch { /* best effort */ }
            _log.Warning("Sender reported failure for {Name}", inc.Name);
            Report(new TransferProgress
            {
                Id = inc.TransferId, Name = inc.Name, IsSending = false,
                Transferred = inc.Received, Total = inc.Size, Done = true, Failed = true
            });
            return;
        }

        inc.Stream.Flush();
        inc.Stream.Dispose();

        string expected = packet.GetString("sha256") ?? "";
        string actual = ComputeSha(inc.Path);
        bool ok = string.IsNullOrEmpty(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            _log.Warning("Checksum mismatch for {Path} (expected {E}, got {A})", inc.Path, expected, actual);
        else
            _log.Information("Received file OK: {Path}", inc.Path);

        Report(new TransferProgress
        {
            Id = inc.TransferId, Name = inc.Name, IsSending = false,
            Transferred = inc.Size, Total = inc.Size, Done = true, Failed = !ok
        });
        FileReceived?.Invoke(this, inc.Path);
    }

    private void Report(TransferProgress p) => Progress?.Invoke(this, p);

    private static string ComputeSha(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
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
}
