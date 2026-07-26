using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Conduit.Core.Logging;
using Conduit.Core.Networking;
using Conduit.Core.Protocol;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// Sends and receives files over the session as a series of base64 chunks
/// (file-offer → file-chunk* → file-complete). Incoming files land in the
/// configured download folder.
/// </summary>
public sealed class FileTransferService
{
    private const int ChunkSize = 64 * 1024; // 64 KB of raw bytes per chunk

    private readonly ILogger _log = ConduitLog.For("FileTransfer");
    private readonly ConduitNode _node;
    private readonly string _downloadFolder;
    private readonly ConcurrentDictionary<string, Incoming> _incoming = new();

    public event EventHandler<string>? FileReceived; // full path

    public FileTransferService(ConduitNode node, string downloadFolder)
    {
        _node = node;
        _downloadFolder = downloadFolder;
        Directory.CreateDirectory(_downloadFolder);
    }

    private sealed class Incoming
    {
        public required string Path;
        public required FileStream Stream;
        public required long Size;
        public long Received;
        public int NextSeq;
        public readonly SortedDictionary<int, byte[]> Pending = new();
    }

    // ---- Sending --------------------------------------------------------------

    public async Task SendFileAsync(string deviceId, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { _log.Warning("File not found: {Path}", path); return; }

        string transferId = Guid.NewGuid().ToString("N");
        _log.Information("Sending {Name} ({Size} bytes) to {Id}", info.Name, info.Length, deviceId);

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
        int seq = 0, read;
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
        }
        sha.TransformFinalBlock([], 0, 0);
        string hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        await _node.SendToAsync(deviceId, Packet.Create(PacketType.FileComplete, b =>
        {
            b["transferId"] = transferId;
            b["ok"] = true;
            b["sha256"] = hash;
        }));
        _log.Information("Finished sending {Name}", info.Name);
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
            Path = dest,
            Stream = File.Create(dest),
            Size = size
        };
        _incoming[transferId] = incoming;
        _log.Information("Receiving {Name} ({Size} bytes) → {Dest}", name, size, dest);
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
    }

    private void CompleteReceive(Packet packet)
    {
        string transferId = packet.GetString("transferId") ?? "";
        if (!_incoming.TryRemove(transferId, out var inc)) return;

        inc.Stream.Flush();
        inc.Stream.Dispose();

        string expected = packet.GetString("sha256") ?? "";
        string actual = ComputeSha(inc.Path);
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            _log.Warning("Checksum mismatch for {Path} (expected {E}, got {A})", inc.Path, expected, actual);
        else
            _log.Information("Received file OK: {Path}", inc.Path);

        FileReceived?.Invoke(this, inc.Path);
    }

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
