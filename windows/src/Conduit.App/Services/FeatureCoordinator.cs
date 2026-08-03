using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Conduit.Core.Logging;
using Conduit.Core.Networking;
using Conduit.Core.Protocol;
using Serilog;

namespace Conduit.App.Services;

/// <summary>
/// The glue between the transport (ConduitNode) and the Windows feature services.
/// Routes every incoming packet to the right handler and pushes local events
/// (clipboard changes) out to connected peers.
/// </summary>
public sealed class FeatureCoordinator
{
    private readonly ILogger _log = ConduitLog.For("Features");
    private readonly ConduitNode _node;
    private readonly ClipboardService _clipboard;
    private readonly MediaService _media;
    private readonly PowerService _power;
    private readonly FileTransferService _files;
    private readonly FileStreamService _stream;
    private readonly FileSearchService _search;
    private readonly NotificationService _notifications;
    private readonly InputService _input = new();

    // Files at least this big take the fast raw-stream path; smaller ones stay on the simple chunked path.
    private const long StreamThreshold = 1 * 1024 * 1024;

    // In-flight searches we're serving for the peer, keyed by requestId, so a cancel can abort them.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _searchCancels = new();

    /// <summary>Latest phone status for the dashboard.</summary>
    public PhoneStatus Status { get; } = new();
    public event EventHandler? StatusChanged;

    /// <summary>Forwards live file-transfer progress to the UI.</summary>
    public event EventHandler<TransferProgress>? FileProgress;

    /// <summary>Results the peer returned for one of our file searches.</summary>
    public event EventHandler<FileSearchResultsEventArgs>? SearchResults;

    /// <summary>A directory listing the peer returned for one of our browse requests.</summary>
    public event EventHandler<DirListingEventArgs>? DirListing;

    public FeatureCoordinator(
        ConduitNode node,
        ClipboardService clipboard,
        MediaService media,
        PowerService power,
        FileTransferService files,
        FileStreamService stream,
        FileSearchService search,
        NotificationService notifications)
    {
        _node = node;
        _clipboard = clipboard;
        _media = media;
        _power = power;
        _files = files;
        _stream = stream;
        _search = search;
        _notifications = notifications;

        _node.PacketReceived += OnPacket;
        _clipboard.LocalClipboardChanged += OnLocalClipboard;
        _files.Progress += (_, p) => FileProgress?.Invoke(this, p);
        _stream.Progress += (_, p) => FileProgress?.Invoke(this, p);
    }

    private void OnLocalClipboard(object? sender, string text)
    {
        _ = _node.BroadcastAsync(Packet.Create(PacketType.Clipboard, b =>
        {
            b["content"] = text;
            b["contentType"] = "text";
        }));
    }

    private void OnPacket(object? sender, PacketEventArgs e)
    {
        var packet = e.Packet;
        try
        {
            switch (packet.Type)
            {
                case PacketType.Clipboard:
                    _clipboard.SetFromRemote(packet.GetString("content") ?? "");
                    break;

                case PacketType.MediaCommand:
                    double? mediaValue = packet.Body["value"] is { } mv
                        && mv.AsValue().TryGetValue<double>(out var mvd) ? mvd : null;
                    _media.Execute(packet.GetString("command") ?? "", mediaValue);
                    break;

                case PacketType.RemoteCommand:
                    _power.Execute(packet.GetString("command") ?? "");
                    break;

                case PacketType.PcInput:
                    HandlePcInput(packet);
                    break;

                case PacketType.FileOffer:
                    // A stream offer means the bytes come over the raw fast port, not as chunks here.
                    if (packet.GetBool("stream"))
                        _stream.RegisterIncoming(packet.GetString("transferId") ?? "", e.Peer.DeviceId,
                            packet.GetString("name") ?? "conduit-file", packet.GetLong("size"));
                    else
                        _files.HandlePacket(packet);
                    break;

                case PacketType.FileChunk:
                case PacketType.FileComplete:
                    _files.HandlePacket(packet);
                    break;

                case PacketType.FileSearch:
                    HandleFileSearch(e.Peer.DeviceId, packet);
                    break;

                case PacketType.FileSearchResult:
                    HandleFileSearchResult(e.Peer.DeviceId, packet);
                    break;

                case PacketType.FileSearchCancel:
                    HandleFileSearchCancel(packet);
                    break;

                case PacketType.FileRequest:
                {
                    var path = _search.Resolve(packet.GetString("id") ?? "");
                    if (path is not null) _ = SendFileAsync(e.Peer.DeviceId, path);
                    else _log.Warning("file-request for unknown id ignored");
                    break;
                }

                case PacketType.DirList:
                    HandleDirList(e.Peer.DeviceId, packet);
                    break;

                case PacketType.DirListResult:
                    HandleDirListResult(e.Peer.DeviceId, packet);
                    break;

                case PacketType.OpenLink:
                    OpenLink(packet.GetString("url") ?? "");
                    break;

                case PacketType.Notification:
                    _notifications.Show(packet);
                    break;

                case PacketType.Battery:
                    Status.BatteryLevel = packet.GetInt("level");
                    Status.Charging = packet.GetBool("charging");
                    Status.Temperature = packet.GetInt("temperature");
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case PacketType.DeviceStatus:
                    Status.Ssid = packet.GetString("ssid") ?? "";
                    Status.Signal = packet.GetInt("signal");
                    Status.RingerMode = packet.GetString("ringerMode") ?? "";
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case PacketType.MediaState:
                    Status.NowPlaying = $"{packet.GetString("title")} — {packet.GetString("artist")}";
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case PacketType.SmsList:
                    _log.Information("Received SMS thread list");
                    break;

                default:
                    _log.Debug("Unhandled packet type {Type}", packet.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error handling {Type} from {Peer}", packet.Type, e.Peer);
        }
    }

    // ---- Outgoing convenience helpers (called from the UI) --------------------

    /// <summary>Sends a file, choosing the fast raw-stream path for big files and chunks for small ones.</summary>
    public Task SendFileAsync(string deviceId, string path)
    {
        try
        {
            if (new FileInfo(path).Length >= StreamThreshold)
                return _stream.SendFileAsync(deviceId, path);
        }
        catch { /* fall through to the chunked path */ }
        return _files.SendFileAsync(deviceId, path);
    }

    public Task SendClipboardAsync(string deviceId, string text) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.Clipboard, b =>
        {
            b["content"] = text;
            b["contentType"] = "text";
        }));

    public Task SendMediaCommandAsync(string deviceId, string command) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.MediaCommand, b => b["command"] = command));

    public Task SendRemoteCommandAsync(string deviceId, string command) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.RemoteCommand, b => b["command"] = command));

    // The phone's touchpad/keyboard drives the PC mouse and typing via one "pc-input" packet.
    private void HandlePcInput(Packet packet)
    {
        switch (packet.GetString("action"))
        {
            case "move":   _input.Move(packet.GetInt("dx"), packet.GetInt("dy")); break;
            case "click":  _input.Click(packet.GetString("button") ?? "left"); break;
            case "scroll": _input.Scroll(packet.GetInt("amount")); break;
            case "text":   _input.Type(packet.GetString("text") ?? ""); break;
            case "key":    _input.Key(packet.GetString("key") ?? ""); break;
        }
    }

    /// <summary>Tells the phone to start streaming its camera (front/back) to this PC's video port.</summary>
    public Task SendWebcamStartAsync(string deviceId, int port, string facing = "front") =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.WebcamStart, b =>
        {
            b["port"] = port;
            b["facing"] = facing;
        }));

    public Task SendWebcamStopAsync(string deviceId) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.WebcamStop));

    /// <summary>Flips the phone between its front and back camera while it is already streaming.</summary>
    public Task SendWebcamSwitchAsync(string deviceId, string facing) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.WebcamSwitch, b => b["facing"] = facing));

    /// <summary>Tells the phone to mirror its screen to this PC's screen-mirror port.</summary>
    public Task SendScreenStartAsync(string deviceId, int port) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.ScreenStart, b => b["port"] = port));

    public Task SendScreenStopAsync(string deviceId) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.ScreenStop));

    /// <summary>Remote-control the phone while mirroring: tap at a normalized (0..1) point.</summary>
    public Task SendInputTapAsync(string deviceId, double x, double y) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.Input, b =>
        {
            b["action"] = "tap"; b["x"] = x; b["y"] = y;
        }));

    /// <summary>Swipe/drag from one normalized point to another over durationMs.</summary>
    public Task SendInputSwipeAsync(string deviceId, double x1, double y1, double x2, double y2, int durationMs) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.Input, b =>
        {
            b["action"] = "swipe";
            b["x"] = x1; b["y"] = y1; b["x2"] = x2; b["y2"] = y2; b["durationMs"] = durationMs;
        }));

    /// <summary>Press a phone key: back / home / recents / enter / backspace.</summary>
    public Task SendInputKeyAsync(string deviceId, string key) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.Input, b =>
        {
            b["action"] = "key"; b["key"] = key;
        }));

    /// <summary>Type text into the phone's focused field.</summary>
    public Task SendInputTextAsync(string deviceId, string text) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.Input, b =>
        {
            b["action"] = "text"; b["text"] = text;
        }));

    public Task SendSmsAsync(string deviceId, string address, string body) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.SmsSend, b =>
        {
            b["address"] = address;
            b["body"] = body;
        }));

    /// <summary>Asks the peer to search its files; results arrive via <see cref="SearchResults"/>.</summary>
    public Task SendFileSearchAsync(string deviceId, string query, string requestId) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.FileSearch, b =>
        {
            b["requestId"] = requestId;
            b["query"] = query;
        }));

    /// <summary>Tells the peer to stop the search with this id (aborts its in-flight walk).</summary>
    public Task SendFileSearchCancelAsync(string deviceId, string requestId) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.FileSearchCancel, b => b["requestId"] = requestId));

    /// <summary>Asks the peer to send a file it returned in a search result.</summary>
    public Task SendFileRequestAsync(string deviceId, string id) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.FileRequest, b => b["id"] = id));

    /// <summary>Asks the peer to list a directory; an empty token lists its roots.</summary>
    public Task SendDirListAsync(string deviceId, string requestId, string token) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.DirList, b =>
        {
            b["requestId"] = requestId;
            b["token"] = token;
        }));

    /// <summary>Asks the peer to open a URL in its browser.</summary>
    public Task SendOpenLinkAsync(string deviceId, string url) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.OpenLink, b => b["url"] = url));

    /// <summary>Opens a peer-supplied URL in the default browser (http/https only).</summary>
    private void OpenLink(string rawUrl)
    {
        var url = NormalizeUrl(rawUrl);
        if (url is null) { _log.Warning("Ignoring non-http open-link request"); return; }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _log.Information("Opened link {Url}", url);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to open link");
        }
    }

    /// <summary>Trims, adds https:// if no scheme, and only allows http/https. Null if invalid.</summary>
    private static string? NormalizeUrl(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return null;
        if (!raw.Contains("://")) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    // ---- File-search packet handling ------------------------------------------

    private void HandleFileSearch(string fromDeviceId, Packet packet)
    {
        var requestId = packet.GetString("requestId") ?? "";
        var query = packet.GetString("query") ?? "";

        // Walk the disk off the receive loop so an incoming file-search-cancel can be processed
        // (and abort this walk) while it runs, rather than being queued behind it.
        var cts = new CancellationTokenSource();
        if (requestId.Length > 0) _searchCancels[requestId] = cts;
        _ = Task.Run(() =>
        {
            try
            {
                var (results, truncated) = _search.Search(query, cts.Token);
                if (cts.IsCancellationRequested) return; // stopped by the peer — don't reply
                _ = _node.SendToAsync(fromDeviceId, Packet.Create(PacketType.FileSearchResult, b =>
                {
                    b["requestId"] = requestId;
                    b["truncated"] = truncated;
                    var arr = new JsonArray();
                    foreach (var r in results)
                        arr.Add(new JsonObject
                        {
                            ["id"] = r.Id,
                            ["name"] = r.Name,
                            ["size"] = r.Size,
                            ["folder"] = r.Folder,
                            ["mime"] = r.Mime,
                        });
                    b["results"] = arr;
                }));
            }
            catch (Exception ex) { _log.Error(ex, "File search failed"); }
            finally
            {
                if (requestId.Length > 0) _searchCancels.TryRemove(requestId, out _);
                cts.Dispose();
            }
        });
    }

    /// <summary>Peer asked us to stop a search it started — abort the matching in-flight walk.</summary>
    private void HandleFileSearchCancel(Packet packet)
    {
        var requestId = packet.GetString("requestId") ?? "";
        if (requestId.Length > 0 && _searchCancels.TryGetValue(requestId, out var cts))
        {
            cts.Cancel();
            _log.Information("File search {RequestId} cancelled by peer", requestId);
        }
    }

    private void HandleFileSearchResult(string fromDeviceId, Packet packet)
    {
        var list = new List<FileSearchService.Result>();
        if (packet.Body["results"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                try
                {
                    list.Add(new FileSearchService.Result(
                        o["id"]?.GetValue<string>() ?? "",
                        o["name"]?.GetValue<string>() ?? "",
                        o["size"]?.GetValue<long>() ?? 0,
                        o["folder"]?.GetValue<string>() ?? "",
                        o["mime"]?.GetValue<string>() ?? ""));
                }
                catch { /* skip a malformed row */ }
            }
        }
        SearchResults?.Invoke(this, new FileSearchResultsEventArgs
        {
            DeviceId = fromDeviceId,
            RequestId = packet.GetString("requestId") ?? "",
            Results = list,
            Truncated = packet.GetBool("truncated"),
        });
    }

    // ---- Remote file browser handling -----------------------------------------

    /// <summary>Peer asked us to list a folder — resolve its token and reply with the entries.</summary>
    private void HandleDirList(string fromDeviceId, Packet packet)
    {
        var requestId = packet.GetString("requestId") ?? "";
        var token = packet.GetString("token") ?? "";
        var listing = _search.List(token);
        _ = _node.SendToAsync(fromDeviceId, Packet.Create(PacketType.DirListResult, b =>
        {
            b["requestId"] = requestId;
            b["token"] = token;
            b["name"] = listing.Name;
            b["path"] = listing.Path;
            if (listing.Parent is not null) b["parent"] = listing.Parent;
            if (listing.Error is not null) b["error"] = listing.Error;
            var arr = new JsonArray();
            foreach (var en in listing.Entries)
                arr.Add(new JsonObject
                {
                    ["name"] = en.Name,
                    ["isDir"] = en.IsDir,
                    ["token"] = en.Token,
                    ["size"] = en.Size,
                    ["mime"] = en.Mime,
                });
            b["entries"] = arr;
        }));
    }

    private void HandleDirListResult(string fromDeviceId, Packet packet)
    {
        var entries = new List<DirEntry>();
        if (packet.Body["entries"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                try
                {
                    entries.Add(new DirEntry(
                        o["token"]?.GetValue<string>() ?? "",
                        o["name"]?.GetValue<string>() ?? "",
                        o["isDir"]?.GetValue<bool>() ?? false,
                        o["size"]?.GetValue<long>() ?? 0,
                        o["mime"]?.GetValue<string>() ?? ""));
                }
                catch { /* skip a malformed row */ }
            }
        }
        DirListing?.Invoke(this, new DirListingEventArgs
        {
            DeviceId = fromDeviceId,
            RequestId = packet.GetString("requestId") ?? "",
            Token = packet.GetString("token") ?? "",
            Name = packet.GetString("name") ?? "",
            Path = packet.GetString("path") ?? "",
            Parent = packet.GetString("parent"),
            Error = packet.GetString("error"),
            Entries = entries,
        });
    }
}

/// <summary>One folder or file in a remote directory listing.</summary>
public sealed record DirEntry(string Token, string Name, bool IsDir, long Size, string Mime);

/// <summary>A directory listing the peer returned, raised for the UI.</summary>
public sealed class DirListingEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public string RequestId { get; init; } = "";
    public string Token { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Parent { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<DirEntry> Entries { get; init; }
}

/// <summary>Search results the peer returned, raised for the UI.</summary>
public sealed class FileSearchResultsEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
    public string RequestId { get; init; } = "";
    public required IReadOnlyList<FileSearchService.Result> Results { get; init; }
    public bool Truncated { get; init; }
}

public sealed class PhoneStatus
{
    public int BatteryLevel { get; set; }
    public bool Charging { get; set; }
    public int Temperature { get; set; }
    public string Ssid { get; set; } = "";
    public int Signal { get; set; }
    public string RingerMode { get; set; } = "";
    public string NowPlaying { get; set; } = "";
}
