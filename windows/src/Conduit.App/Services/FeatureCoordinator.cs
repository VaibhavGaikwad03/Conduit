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
    private readonly NotificationService _notifications;

    /// <summary>Latest phone status for the dashboard.</summary>
    public PhoneStatus Status { get; } = new();
    public event EventHandler? StatusChanged;

    public FeatureCoordinator(
        ConduitNode node,
        ClipboardService clipboard,
        MediaService media,
        PowerService power,
        FileTransferService files,
        NotificationService notifications)
    {
        _node = node;
        _clipboard = clipboard;
        _media = media;
        _power = power;
        _files = files;
        _notifications = notifications;

        _node.PacketReceived += OnPacket;
        _clipboard.LocalClipboardChanged += OnLocalClipboard;
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

    public Task SendSmsAsync(string deviceId, string address, string body) =>
        _node.SendToAsync(deviceId, Packet.Create(PacketType.SmsSend, b =>
        {
            b["address"] = address;
            b["body"] = body;
        }));
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
