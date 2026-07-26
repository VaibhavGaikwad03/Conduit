namespace Conduit.Core.Models;

public enum DeviceType
{
    Unknown,
    Android,
    Windows
}

public enum ConnectionState
{
    Disconnected,
    Discovered,
    Connecting,
    Handshaking,
    Connected,
    Paired
}

/// <summary>A device seen on the network (from a discovery beacon or an active connection).</summary>
public sealed class DeviceInfo
{
    public required string DeviceId { get; init; }
    public required string Name { get; set; }
    public DeviceType Type { get; set; } = DeviceType.Unknown;
    public string? IpAddress { get; set; }
    public int TcpPort { get; set; } = ConduitPorts.Tcp;
    public int Protocol { get; set; } = 1;

    /// <summary>Last time we heard a beacon or packet from this device.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    public ConnectionState State { get; set; } = ConnectionState.Discovered;
    public bool IsPaired { get; set; }

    public override string ToString() => $"{Name} ({Type}, {DeviceId[..Math.Min(8, DeviceId.Length)]})";
}

/// <summary>A remembered, trusted peer persisted to disk.</summary>
public sealed class PairedDevice
{
    public required string DeviceId { get; init; }
    public required string Name { get; set; }
    public DeviceType Type { get; set; }
    /// <summary>Base64 public key of the peer, used to re-establish encrypted sessions.</summary>
    public required string PublicKey { get; init; }
    public DateTimeOffset PairedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class ConduitPorts
{
    public const int Udp = 5461;
    public const int Tcp = 5462;
    public const int ProtocolVersion = 1;
}
