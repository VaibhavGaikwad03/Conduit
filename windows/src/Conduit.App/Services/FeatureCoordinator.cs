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
    private readonly FileSearchService _search;
    private readonly NotificationService _notifications;

    /// <summary>Latest phone status for the dashboard.</summary>
    public PhoneStatus Status { get; } = new();
    public event EventHandler? StatusChanged;

    /// <summary>Forwards live file-transfer progress to the UI.</summary>
    public event EventHandler<TransferProgress>? FileProgress;

    /// <summary>Results the peer returned for one of our file searches.</summary>
    public event EventHandler<FileSearchResultsEventArgs>? SearchResults;

    public FeatureCoordinator(
        ConduitNode node,
        ClipboardService clipboard,
        MediaService media,
        PowerService power,
        FileTransferService files,
        FileSearchService search,
        NotificationService notifications)
    {
        _node = node;
        _clipboard = clipboard;
        _media = media;
        _power = power;
        _files = files;
        _search = search;
        _notifications = notifications;

        _node.PacketReceived += OnPacket;
        _clipboard.LocalClipboardChanged += OnLocalClipboard;
        _files.Progress += (_, p) => FileProgress?.Invoke(this, p);
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

                case PacketType.FileOffer:
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

                case PacketType.FileRequest:
                {
                    var path = _search.Resolve(packet.GetString("id") ?? "");
                    if (path is not null) _ = _files.SendFileAsync(e.Peer.DeviceId, path);
                    else _log.Warning("file-request for unknown id ignored");
                    break;
                }

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

    public Task SendFileAsync(string deviceId, string path) => _files.SendFileAsync(deviceId, path);

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

    /// <summary>Tells the phone to start streaming its camera to this PC's video port.</summary>
    public Task SendWebcamStartAsync(string deviceId, int port) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.WebcamStart, b => b["port"] = port));

    public Task SendWebcamStopAsync(string deviceId) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.WebcamStop));

    public Task SendSmsAsync(string deviceId, string address, string body) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.SmsSend, b =>
        {
            b["address"] = address;
            b["body"] = body;
        }));

    /// <summary>Asks the peer to search its files; results arrive via <see cref="SearchResults"/>.</summary>
    public Task SendFileSearchAsync(string deviceId, string query) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.FileSearch, b =>
        {
            b["requestId"] = Guid.NewGuid().ToString("N");
            b["query"] = query;
        }));

    /// <summary>Asks the peer to send a file it returned in a search result.</summary>
    public Task SendFileRequestAsync(string deviceId, string id) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.FileRequest, b => b["id"] = id));

    // ---- File-search packet handling ------------------------------------------

    private void HandleFileSearch(string fromDeviceId, Packet packet)
    {
        var (results, truncated) = _search.Search(packet.GetString("query") ?? "");
        _ = _node.SendToAsync(fromDeviceId, Packet.Create(PacketType.FileSearchResult, b =>
        {
            b["requestId"] = packet.GetString("requestId") ?? "";
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
            Results = list,
            Truncated = packet.GetBool("truncated"),
        });
    }
}

/// <summary>Search results the peer returned, raised for the UI.</summary>
public sealed class FileSearchResultsEventArgs : EventArgs
{
    public required string DeviceId { get; init; }
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
