using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit.Core.Protocol;

/// <summary>Canonical list of packet <c>type</c> values. Must match PROTOCOL.md and the Android side.</summary>
public static class PacketType
{
    public const string Identity = "identity";
    public const string PairRequest = "pair-request";
    public const string PairResponse = "pair-response";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Clipboard = "clipboard";
    public const string FileOffer = "file-offer";
    public const string FileChunk = "file-chunk";
    public const string FileComplete = "file-complete";
    public const string Notification = "notification";
    public const string NotificationAction = "notification-action";
    public const string MediaState = "media-state";
    public const string MediaCommand = "media-command";
    public const string RemoteCommand = "remote-command";
    public const string Battery = "battery";
    public const string DeviceStatus = "device-status";
    public const string SmsList = "sms-list";
    public const string SmsSend = "sms-send";
    public const string Error = "error";
}

/// <summary>
/// The envelope every message travels in: { id, type, ts, body }. The body is an
/// arbitrary JSON object accessed via the typed helpers below.
/// </summary>
public sealed class Packet
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Type { get; init; }
    public long Ts { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public JsonObject Body { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public static Packet Create(string type, Action<JsonObject>? build = null)
    {
        var body = new JsonObject();
        build?.Invoke(body);
        return new Packet { Type = type, Body = body };
    }

    public string? GetString(string key) => Body[key]?.GetValue<string>();
    public long GetLong(string key, long fallback = 0) =>
        Body[key] is { } n && n.AsValue().TryGetValue<long>(out var v) ? v : fallback;
    public int GetInt(string key, int fallback = 0) => (int)GetLong(key, fallback);
    public bool GetBool(string key, bool fallback = false) =>
        Body[key] is { } n && n.AsValue().TryGetValue<bool>(out var v) ? v : fallback;

    public string ToJson()
    {
        var obj = new JsonObject
        {
            ["id"] = Id,
            ["type"] = Type,
            ["ts"] = Ts,
            ["body"] = Body.DeepClone()
        };
        return obj.ToJsonString(JsonOpts);
    }

    public static Packet FromJson(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
                   ?? throw new FormatException("Packet JSON is not an object");
        return new Packet
        {
            Id = node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            Type = node["type"]?.GetValue<string>() ?? PacketType.Error,
            Ts = node["ts"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body = node["body"]?.AsObject() ?? new JsonObject()
        };
    }
}
