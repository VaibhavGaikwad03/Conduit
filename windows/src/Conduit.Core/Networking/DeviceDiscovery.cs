using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Serilog;

namespace Conduit.Core.Networking;

/// <summary>Raised when a discovery beacon is received from any device on the LAN.</summary>
public sealed class BeaconEventArgs(DeviceInfo device) : EventArgs
{
    public DeviceInfo Device { get; } = device;
}

/// <summary>
/// UDP presence layer. Periodically broadcasts this device's identity beacon on port 5461
/// and listens for beacons from other devices. See PROTOCOL.md §1.
/// </summary>
public sealed class DeviceDiscovery : IDisposable
{
    private readonly ILogger _log = ConduitLog.For("Discovery");
    private readonly DeviceInfo _self;
    private readonly UdpClient _listener;
    private readonly UdpClient _broadcaster;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _broadcastTask;

    public event EventHandler<BeaconEventArgs>? BeaconReceived;

    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(3);

    public DeviceDiscovery(DeviceInfo self)
    {
        _self = self;
        _listener = new UdpClient
        {
            EnableBroadcast = true,
            ExclusiveAddressUse = false
        };
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, ConduitPorts.Udp));

        _broadcaster = new UdpClient { EnableBroadcast = true };
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        _broadcastTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));
        _log.Information("Discovery started on UDP {Port}", ConduitPorts.Udp);
    }

    /// <summary>Send a single beacon immediately (e.g. on startup or when a peer connects).</summary>
    public async Task AnnounceAsync()
    {
        try
        {
            var beacon = new JsonObject
            {
                ["conduit"] = 1,
                ["deviceId"] = _self.DeviceId,
                ["name"] = _self.Name,
                ["type"] = "windows",
                ["tcpPort"] = _self.TcpPort,
                ["protocol"] = ConduitPorts.ProtocolVersion
            };
            byte[] data = Encoding.UTF8.GetBytes(beacon.ToJsonString());
            // Send to every active interface's directed broadcast (e.g. 192.168.43.255) so the
            // beacon reaches hotspot/tether subnets, which the limited 255.255.255.255 broadcast
            // often does not. Keep the limited broadcast too as a fallback. See PROTOCOL.md §1.
            foreach (var target in BroadcastTargets())
            {
                try
                {
                    await _broadcaster.SendAsync(data, data.Length, new IPEndPoint(target, ConduitPorts.Udp))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Verbose(ex, "Beacon send failed for {Target}", target);
                }
            }
            _log.Debug("Beacon announced");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to send beacon");
        }
    }

    /// <summary>Directed broadcast address of each active IPv4 interface, plus the limited broadcast.</summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null)
                        continue;

                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    if (ip.Length != 4 || mask.Length != 4) continue;

                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcast[i] = (byte)(ip[i] | (mask[i] ^ 0xFF));
                    targets.Add(new IPAddress(bcast));
                }
            }
        }
        catch
        {
            // Fall back to the limited broadcast below if enumeration fails.
        }
        targets.Add(IPAddress.Broadcast);
        return targets.Distinct();
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await AnnounceAsync().ConfigureAwait(false);
            try { await Task.Delay(BroadcastInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
                HandleBeacon(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "Error receiving beacon");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void HandleBeacon(byte[] buffer, IPEndPoint from)
    {
        try
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(buffer))?.AsObject();
            if (json is null || json["conduit"] is null) return;

            string deviceId = json["deviceId"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(deviceId) || deviceId == _self.DeviceId)
                return; // ignore our own beacons

            var device = new DeviceInfo
            {
                DeviceId = deviceId,
                Name = json["name"]?.GetValue<string>() ?? "Unknown",
                Type = json["type"]?.GetValue<string>() == "android" ? DeviceType.Android : DeviceType.Windows,
                IpAddress = from.Address.ToString(),
                TcpPort = json["tcpPort"]?.GetValue<int>() ?? ConduitPorts.Tcp,
                Protocol = json["protocol"]?.GetValue<int>() ?? 1,
                LastSeen = DateTimeOffset.UtcNow
            };

            _log.Debug("Beacon from {Device} @ {Ip}", device, device.IpAddress);
            BeaconReceived?.Invoke(this, new BeaconEventArgs(device));
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "Ignoring malformed beacon from {From}", from);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Dispose(); } catch { /* ignore */ }
        try { _broadcaster.Dispose(); } catch { /* ignore */ }
        _cts?.Dispose();
        _log.Information("Discovery stopped");
    }
}
