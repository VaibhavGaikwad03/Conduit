using System.Net.Sockets;
using System.Text;
using Conduit.Core.Logging;
using Conduit.Core.Models;
using Conduit.Core.Protocol;
using Conduit.Core.Security;
using Serilog;

namespace Conduit.Core.Networking;

/// <summary>
/// A single encrypted TCP session with one peer. Performs the identity handshake
/// (PROTOCOL.md §6), derives the AES session key, then pumps encrypted packets.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly ILogger _log = ConduitLog.For("Connection");
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CryptoService _crypto;
    private readonly DeviceInfo _self;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SessionCipher? _cipher;
    private CancellationTokenSource? _cts;

    public DeviceInfo? Peer { get; private set; }
    public bool IsHandshaked => _cipher is not null;

    public event EventHandler<Packet>? PacketReceived;
    public event EventHandler<DeviceInfo>? Handshaked;
    public event EventHandler? Disconnected;

    public PeerConnection(TcpClient client, CryptoService crypto, DeviceInfo self)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _crypto = crypto;
        _self = self;
    }

    /// <summary>Run the handshake then loop reading packets until the peer disconnects.</summary>
    public async Task RunAsync(CancellationToken external)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;
        try
        {
            await SendIdentityAsync(ct).ConfigureAwait(false);
            await ReceiveIdentityAsync(ct).ConfigureAwait(false);
            await ReadLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.Warning(ex, "Session with {Peer} ended with error", Peer);
        }
        finally
        {
            _log.Information("Disconnected from {Peer}", Peer);
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SendIdentityAsync(CancellationToken ct)
    {
        var identity = Packet.Create(PacketType.Identity, b =>
        {
            b["deviceId"] = _self.DeviceId;
            b["name"] = _self.Name;
            b["deviceType"] = "windows";
            b["protocol"] = ConduitPorts.ProtocolVersion;
            b["publicKey"] = _crypto.PublicKeyBase64;
        });
        // Handshake frames are plaintext (no session key yet).
        await FrameCodec.WriteFrameAsync(_stream, Encoding.UTF8.GetBytes(identity.ToJson()), ct)
            .ConfigureAwait(false);
        _log.Debug("Sent identity");
    }

    private async Task ReceiveIdentityAsync(CancellationToken ct)
    {
        var frame = await FrameCodec.ReadFrameAsync(_stream, ct).ConfigureAwait(false)
                    ?? throw new IOException("Peer closed during handshake");
        var packet = Packet.FromJson(Encoding.UTF8.GetString(frame));
        if (packet.Type != PacketType.Identity)
            throw new InvalidDataException($"Expected identity, got {packet.Type}");

        string peerKey = packet.GetString("publicKey")
                         ?? throw new InvalidDataException("Identity missing publicKey");

        Peer = new DeviceInfo
        {
            DeviceId = packet.GetString("deviceId") ?? "unknown",
            Name = packet.GetString("name") ?? "Unknown",
            Type = packet.GetString("deviceType") == "android" ? DeviceType.Android : DeviceType.Windows,
            IpAddress = (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString(),
            Protocol = packet.GetInt("protocol", 1),
            State = ConnectionState.Connected
        };

        byte[] sessionKey = _crypto.DeriveSessionKey(peerKey);
        _cipher = new SessionCipher(sessionKey);

        _log.Information("Handshake complete with {Peer}; session encrypted", Peer);
        Handshaked?.Invoke(this, Peer);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await FrameCodec.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
            if (frame is null)
            {
                _log.Debug("Peer {Peer} closed the stream", Peer);
                break;
            }

            Packet packet;
            try
            {
                byte[] plain = _cipher!.Decrypt(frame);
                packet = Packet.FromJson(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to decrypt/parse frame from {Peer}", Peer);
                continue;
            }

            if (packet.Type == PacketType.Ping)
            {
                await SendAsync(Packet.Create(PacketType.Pong), ct).ConfigureAwait(false);
                continue;
            }
            if (packet.Type == PacketType.Pong)
                continue; // heartbeat ack — nothing to route

            _log.Verbose("Recv {Type} from {Peer}", packet.Type, Peer);
            PacketReceived?.Invoke(this, packet);
        }
    }

    /// <summary>Encrypt and send a packet to the peer.</summary>
    public async Task SendAsync(Packet packet, CancellationToken ct = default)
    {
        if (_cipher is null)
            throw new InvalidOperationException("Cannot send before handshake completes");

        byte[] cipherFrame = _cipher.Encrypt(Encoding.UTF8.GetBytes(packet.ToJson()));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, cipherFrame, ct).ConfigureAwait(false);
            _log.Verbose("Sent {Type} to {Peer}", packet.Type, Peer);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
        _writeLock.Dispose();
        _cts?.Dispose();
        await Task.CompletedTask;
    }
}
