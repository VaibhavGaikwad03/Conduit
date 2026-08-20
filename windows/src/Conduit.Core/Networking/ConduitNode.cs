using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Conduit.Core.Protocol;
using Conduit.Core.Security;
using Conduit.Core.Storage;
using Serilog;

namespace Conduit.Core.Networking;

public sealed class PacketEventArgs(DeviceInfo peer, Packet packet) : EventArgs
{
    public DeviceInfo Peer { get; } = peer;
    public Packet Packet { get; } = packet;
}

public sealed class PairingRequestEventArgs(DeviceInfo peer, string code) : EventArgs
{
    public DeviceInfo Peer { get; } = peer;
    public string Code { get; } = code;
    /// <summary>Set by the UI to accept/reject the pairing.</summary>
    public bool Accepted { get; set; }
}

/// <summary>
/// The engine that ties discovery, connections, pairing and the encrypted session together.
/// The UI layer creates one of these, subscribes to its events, and calls SendToAsync.
/// </summary>
public sealed class ConduitNode : IAsyncDisposable
{
    private readonly ILogger _log = ConduitLog.For("Node");
    private readonly AppStore _store;
    private readonly CryptoService _crypto;
    private readonly DeviceInfo _self;
    private readonly DeviceDiscovery _discovery;
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _known = new();

    // Devices the user manually disconnected: don't auto-reconnect until they reconnect.
    private readonly ConcurrentDictionary<string, byte> _suppressReconnect = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _heartbeatTask;

    public IReadOnlyCollection<DeviceInfo> KnownDevices => _known.Values.ToList();
    public DeviceInfo Self => _self;

    public event EventHandler? DevicesChanged;
    public event EventHandler<DeviceInfo>? PeerConnected;
    public event EventHandler<DeviceInfo>? PeerDisconnected;
    public event EventHandler<PacketEventArgs>? PacketReceived;
    public event EventHandler<PairingRequestEventArgs>? PairingRequested;

    public ConduitNode(AppStore store)
    {
        _store = store;
        _crypto = CryptoService.LoadOrCreate(store.Config.PrivateKey, out var privateKey);
        if (store.Config.PrivateKey != privateKey)
        {
            store.Config.PrivateKey = privateKey;
            store.Save();
        }

        _self = new DeviceInfo
        {
            DeviceId = store.Config.DeviceId,
            Name = store.Config.DeviceName,
            Type = DeviceType.Windows,
            TcpPort = ConduitPorts.Tcp
        };
        _discovery = new DeviceDiscovery(_self);
        _discovery.BeaconReceived += OnBeaconReceived;
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Any, ConduitPorts.Tcp);
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _discovery.Start();
        await _discovery.AnnounceAsync().ConfigureAwait(false);

        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        _log.Information("Conduit node '{Name}' started on TCP {Port}", _self.Name, ConduitPorts.Tcp);
    }

    // ---- Incoming connections -------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _log.Debug("Incoming TCP from {Remote}", client.Client.RemoteEndPoint);
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "Accept loop error");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var conn = new PeerConnection(client, _crypto, _self);
        WireConnection(conn);
        await conn.RunAsync(ct).ConfigureAwait(false);
    }

    // ---- Outgoing connections (triggered by beacons) --------------------------

    private void OnBeaconReceived(object? sender, BeaconEventArgs e)
    {
        var device = e.Device;
        bool isNew = !_known.ContainsKey(device.DeviceId);
        _known.AddOrUpdate(device.DeviceId, device, (_, existing) =>
        {
            existing.Name = device.Name;
            existing.IpAddress = device.IpAddress;
            existing.TcpPort = device.TcpPort;
            existing.LastSeen = DateTimeOffset.UtcNow;
            return existing;
        });
        device.IsPaired = _store.IsPaired(device.DeviceId);

        if (isNew)
        {
            _log.Information("Discovered {Device}", device);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        // Auto-connect to paired peers that aren't connected yet — unless the user
        // manually disconnected this one. Only one side dials (the smaller DeviceId);
        // the other only accepts. Both sides auto-dialing races to open two sockets at
        // once and can "glare" — each keeps a different one and closes the other — which
        // churns the session and breaks transfers. Explicit/pairing connects bypass this.
        if (device.IsPaired && !_peers.ContainsKey(device.DeviceId) && device.IpAddress is not null
            && !_suppressReconnect.ContainsKey(device.DeviceId)
            && string.CompareOrdinal(_self.DeviceId, device.DeviceId) < 0)
            _ = ConnectAsync(device);
    }

    public async Task ConnectAsync(DeviceInfo device)
    {
        // An explicit connect clears any manual-disconnect suppression.
        _suppressReconnect.TryRemove(device.DeviceId, out _);
        if (_peers.ContainsKey(device.DeviceId) || device.IpAddress is null) return;
        try
        {
            _log.Information("Connecting to {Device} @ {Ip}:{Port}", device, device.IpAddress, device.TcpPort);
            var client = new TcpClient();
            await client.ConnectAsync(device.IpAddress, device.TcpPort).ConfigureAwait(false);
            var conn = new PeerConnection(client, _crypto, _self);
            WireConnection(conn);
            _ = conn.RunAsync(_cts!.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to connect to {Device}", device);
        }
    }

    private void WireConnection(PeerConnection conn)
    {
        conn.Handshaked += (_, peer) =>
        {
            // A completed session means someone reconnected on purpose — allow auto-reconnect again.
            _suppressReconnect.TryRemove(peer.DeviceId, out byte _);
            peer.IsPaired = _store.IsPaired(peer.DeviceId);

            // Keep exactly one live session per peer. A burst of duplicate connections
            // (both sides dialing, or several beacons before the first handshake lands)
            // would otherwise let a single file's packets race across sockets and
            // truncate session-based transfers. Adopt the first connection to arrive and
            // drop any later duplicate — never the established one, which may be mid-transfer.
            if (!_peers.TryAdd(peer.DeviceId, conn))
            {
                _log.Information("Duplicate session with {Peer}; dropping the redundant connection", peer);
                _ = conn.DisposeAsync();
                return;
            }

            _known[peer.DeviceId] = peer;
            _log.Information("Peer connected: {Peer} (paired={Paired})", peer, peer.IsPaired);
            PeerConnected?.Invoke(this, peer);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        conn.PacketReceived += (_, packet) => OnPacket(conn, packet);
        conn.Disconnected += (_, _) =>
        {
            if (conn.Peer is not { } p) return;

            // Only the currently-registered connection owns the peer entry. A superseded
            // duplicate that closes must not evict the live session or fire "disconnected".
            bool wasCurrent = ((ICollection<KeyValuePair<string, PeerConnection>>)_peers)
                .Remove(new KeyValuePair<string, PeerConnection>(p.DeviceId, conn));
            if (!wasCurrent) return;

            p.State = ConnectionState.Disconnected;
            PeerDisconnected?.Invoke(this, p);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void OnPacket(PeerConnection conn, Packet packet)
    {
        if (conn.Peer is not { } peer) return;

        switch (packet.Type)
        {
            case PacketType.PairRequest:
                HandlePairRequest(conn, peer, packet);
                return;
            case PacketType.PairResponse:
                HandlePairResponse(peer, packet);
                return;
            case PacketType.Disconnect:
                _log.Information("{Peer} disconnected", peer);
                _suppressReconnect[peer.DeviceId] = 1;
                _ = conn.DisposeAsync();
                return;
            default:
                // Security gate: only paired peers may use features. An unpaired peer can still
                // complete the handshake and exchange pair-request/response (handled above), but
                // every feature packet is dropped until it's actually paired.
                if (!_store.IsPaired(peer.DeviceId))
                {
                    _log.Warning("Dropping {Type} from unpaired peer {Peer}", packet.Type, peer);
                    return;
                }
                PacketReceived?.Invoke(this, new PacketEventArgs(peer, packet));
                return;
        }
    }

    // ---- Pairing --------------------------------------------------------------

    private void HandlePairRequest(PeerConnection conn, DeviceInfo peer, Packet packet)
    {
        string code = packet.GetString("code") ?? "------";
        string publicKey = packet.GetString("publicKey") ?? "";
        _log.Information("Pair request from {Peer}, code {Code}", peer, code);

        var args = new PairingRequestEventArgs(peer, code);
        PairingRequested?.Invoke(this, args);

        bool accepted = args.Accepted || _store.Config.AutoAcceptFromPaired && _store.IsPaired(peer.DeviceId);
        if (accepted && !string.IsNullOrEmpty(publicKey))
        {
            _store.AddPaired(new PairedDevice
            {
                DeviceId = peer.DeviceId, Name = peer.Name, Type = peer.Type, PublicKey = publicKey
            });
            peer.IsPaired = true;
        }

        var response = Packet.Create(PacketType.PairResponse, b =>
        {
            b["accepted"] = accepted;
            b["publicKey"] = _crypto.PublicKeyBase64;
        });
        if (accepted)
        {
            _ = conn.SendAsync(response);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // Reject: send the refusal, then drop the session so it doesn't linger as "connected".
            _ = Task.Run(async () =>
            {
                try { await conn.SendAsync(response); } catch { /* best effort */ }
                await conn.DisposeAsync();
            });
        }
    }

    private void HandlePairResponse(DeviceInfo peer, Packet packet)
    {
        bool accepted = packet.GetBool("accepted");
        string publicKey = packet.GetString("publicKey") ?? "";
        _log.Information("Pair response from {Peer}: accepted={Accepted}", peer, accepted);
        if (accepted && !string.IsNullOrEmpty(publicKey))
        {
            _store.AddPaired(new PairedDevice
            {
                DeviceId = peer.DeviceId, Name = peer.Name, Type = peer.Type, PublicKey = publicKey
            });
            peer.IsPaired = true;
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Begin pairing with a discovered device. Returns the 6-digit code to show the user.</summary>
    public async Task<string> StartPairingAsync(DeviceInfo device)
    {
        if (!_peers.TryGetValue(device.DeviceId, out var conn))
        {
            await ConnectAsync(device).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
            _peers.TryGetValue(device.DeviceId, out conn);
        }
        if (conn is null) throw new InvalidOperationException("Not connected to device");

        string code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        await conn.SendAsync(Packet.Create(PacketType.PairRequest, b =>
        {
            b["publicKey"] = _crypto.PublicKeyBase64;
            b["code"] = code;
        })).ConfigureAwait(false);
        _log.Information("Started pairing with {Device}, code {Code}", device, code);
        return code;
    }

    // ---- Sending --------------------------------------------------------------

    public bool IsConnected(string deviceId) => _peers.ContainsKey(deviceId);

    /// <summary>The last-known IP of a device, for opening a side channel (e.g. the file stream).</summary>
    public string? IpFor(string deviceId) =>
        _known.TryGetValue(deviceId, out var d) ? d.IpAddress : null;

    /// <summary>
    /// The AES-256 session key shared with a paired peer, derived from its stored public key.
    /// It's deterministic (ECDH), so the file-stream side channel can encrypt with the same key
    /// the main session uses, without holding a reference to the live connection.
    /// </summary>
    public byte[]? SessionKeyFor(string deviceId)
    {
        var pub = _store.GetPaired(deviceId)?.PublicKey;
        if (string.IsNullOrEmpty(pub)) return null;
        try { return _crypto.DeriveSessionKey(pub); } catch { return null; }
    }

    /// <summary>
    /// Drop the live session with a device and stop auto-reconnecting to it until the user
    /// explicitly connects again (or the app restarts).
    /// </summary>
    public async Task DisconnectAsync(string deviceId)
    {
        _suppressReconnect[deviceId] = 1;
        if (_peers.TryGetValue(deviceId, out var conn))
        {
            _log.Information("Disconnecting from {Id} (manual)", deviceId);
            // Tell the peer so it also stops auto-reconnecting, then close.
            try { await conn.SendAsync(Packet.Create(PacketType.Disconnect)).ConfigureAwait(false); }
            catch (Exception ex) { _log.Debug(ex, "Could not send disconnect notice to {Id}", deviceId); }
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<bool> SendToAsync(string deviceId, Packet packet)
    {
        if (_peers.TryGetValue(deviceId, out var conn))
        {
            try
            {
                await conn.SendAsync(packet).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Send to {Id} failed", deviceId);
            }
        }
        else
        {
            _log.Debug("No active connection to {Id}; dropping {Type}", deviceId, packet.Type);
        }
        return false;
    }

    public async Task BroadcastAsync(Packet packet)
    {
        foreach (var id in _peers.Keys)
            await SendToAsync(id, packet).ConfigureAwait(false);
    }

    // ---- Heartbeat ------------------------------------------------------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (var id in _peers.Keys)
                await SendToAsync(id, Packet.Create(PacketType.Ping)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        foreach (var conn in _peers.Values)
            await conn.DisposeAsync().ConfigureAwait(false);
        _discovery.Dispose();
        _listener?.Stop();
        _cts?.Dispose();
        _log.Information("Conduit node stopped");
    }
}
