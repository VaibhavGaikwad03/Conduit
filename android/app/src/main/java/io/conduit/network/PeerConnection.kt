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
import kotlinx.coroutines.channels.Channel
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
    // All outgoing packets funnel through one FIFO queue drained by a single writer, so the
    // bytes hit the socket in the exact order send() was called. Without this, launching a
    // coroutine per packet let them race — a file's FILE_COMPLETE could overtake its chunks,
    // and the receiver would finalise an empty/truncated file.
    private val sendQueue = Channel<Packet>(Channel.UNLIMITED)
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
                scope.launch { writeLoop() }
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
            if (packet.type == PacketType.PONG) continue // heartbeat ack — nothing to route
            onPacket?.invoke(packet)
        }
    }

    fun send(packet: Packet) {
        if (cipher == null) { log.w("Cannot send ${packet.type} before handshake"); return }
        // Hand off to the single writer; ordering is preserved by the queue, not by luck.
        sendQueue.trySend(packet)
    }

    /** The one place packets are written. Drains the queue in FIFO order so socket order == send order. */
    private suspend fun writeLoop() {
        val c = cipher ?: return
        try {
            for (packet in sendQueue) {
                try {
                    val frame = c.encrypt(packet.toJson().toByteArray())
                    writeLock.withLock { FrameCodec.writeFrame(output, frame) }
                } catch (e: Exception) {
                    log.w(e, "Send ${packet.type} failed")
                }
            }
        } catch (_: Exception) {
            // Channel closed / scope cancelled — nothing more to write.
        }
    }

    val isHandshaked get() = cipher != null

    /** Send one final packet (best-effort), flush it, then close — used for a graceful disconnect. */
    fun closeWith(packet: Packet) {
        val c = cipher ?: return close()
        scope.launch {
            try {
                val frame = c.encrypt(packet.toJson().toByteArray())
                writeLock.withLock {
                    FrameCodec.writeFrame(output, frame)
                    output.flush()
                }
            } catch (e: Exception) {
                log.w(e, "Farewell send failed")
            } finally {
                close()
            }
        }
    }

    fun close() {
        sendQueue.close()
        scope.cancel()
        runCatching { socket.close() }
    }
}
