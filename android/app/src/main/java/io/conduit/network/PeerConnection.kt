package io.conduit.network

import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.model.DeviceType
import io.conduit.model.Ports
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.net.Socket

/**
 * A single encrypted TCP session with one peer: identity handshake → AES session → packet pump.
 * Mirrors the .NET PeerConnection.
 */
class PeerConnection(
    private val socket: Socket,
    private val crypto: CryptoService,
    private val self: DeviceInfo,
) {
    private val log = ConduitLog.tag("Connection")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val writeLock = Mutex()
    private val input = socket.getInputStream()
    private val output = socket.getOutputStream()

    private var cipher: SessionCipher? = null
    var peer: DeviceInfo? = null
        private set

    var onPacket: ((Packet) -> Unit)? = null
    var onHandshaked: ((DeviceInfo) -> Unit)? = null
    var onDisconnected: (() -> Unit)? = null

    fun start() {
        scope.launch {
            try {
                sendIdentity()
                receiveIdentity()
                readLoop()
            } catch (e: Exception) {
                log.w(e, "Session with ${peer?.name} ended")
            } finally {
                log.i("Disconnected from ${peer?.name}")
                onDisconnected?.invoke()
                close()
            }
        }
    }

    private fun sendIdentity() {
        val identity = Packet.create(PacketType.IDENTITY) {
            put("deviceId", self.deviceId)
            put("name", self.name)
            put("deviceType", "android")
            put("protocol", Ports.PROTOCOL_VERSION)
            put("publicKey", crypto.publicKeyBase64)
        }
        FrameCodec.writeFrame(output, identity.toJson().toByteArray())
        log.d("Sent identity")
    }

    private fun receiveIdentity() {
        val frame = FrameCodec.readFrame(input) ?: throw IllegalStateException("Peer closed during handshake")
        val packet = Packet.fromJson(String(frame))
        require(packet.type == PacketType.IDENTITY) { "Expected identity, got ${packet.type}" }
        val peerKey = packet.getString("publicKey") ?: throw IllegalStateException("Identity missing publicKey")

        peer = DeviceInfo(
            deviceId = packet.getString("deviceId") ?: "unknown",
            name = packet.getString("name") ?: "Unknown",
            type = if (packet.getString("deviceType") == "windows") DeviceType.WINDOWS else DeviceType.ANDROID,
            ipAddress = socket.inetAddress?.hostAddress,
            protocol = packet.getInt("protocol", 1),
        )
        cipher = SessionCipher(crypto.deriveSessionKey(peerKey))
        log.i("Handshake complete with ${peer?.name}; session encrypted")
        onHandshaked?.invoke(peer!!)
    }

    private fun readLoop() {
        while (true) {
            val frame = FrameCodec.readFrame(input) ?: break
            val packet = try {
                Packet.fromJson(String(cipher!!.decrypt(frame)))
            } catch (e: Exception) {
                log.e(e, "Failed to decrypt/parse frame")
                continue
            }
            if (packet.type == PacketType.PING) {
                send(Packet.create(PacketType.PONG))
                continue
            }
            onPacket?.invoke(packet)
        }
    }

    fun send(packet: Packet) {
        val c = cipher ?: run { log.w("Cannot send ${packet.type} before handshake"); return }
        scope.launch {
            try {
                val frame = c.encrypt(packet.toJson().toByteArray())
                writeLock.withLock { FrameCodec.writeFrame(output, frame) }
            } catch (e: Exception) {
                log.w(e, "Send ${packet.type} failed")
            }
        }
    }

    val isHandshaked get() = cipher != null

    fun close() {
        scope.cancel()
        runCatching { socket.close() }
    }
}
