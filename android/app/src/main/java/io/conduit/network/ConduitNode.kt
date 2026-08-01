package io.conduit.network

import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.model.DeviceType
import io.conduit.model.PairedDevice
import io.conduit.model.Ports
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.storage.AppStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import kotlin.random.Random

/**
 * The engine tying discovery, connections, pairing and the encrypted session together.
 * The service creates one, wires its callbacks, and calls sendTo / broadcast.
 * Mirrors the .NET ConduitNode.
 */
class ConduitNode(private val store: AppStore) {
    private val log = ConduitLog.tag("Node")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val crypto: CryptoService = CryptoService.loadOrCreate(store.privateKey, store.publicKey).also {
        store.privateKey = it.privateKeyBase64
        store.publicKey = it.publicKeyBase64
    }

    val self = DeviceInfo(
        deviceId = store.deviceId,
        name = store.deviceName,
        type = DeviceType.ANDROID,
        tcpPort = Ports.TCP,
    )

    private val peers = ConcurrentHashMap<String, PeerConnection>()
    private val known = ConcurrentHashMap<String, DeviceInfo>()

    // Devices the user manually disconnected: don't auto-reconnect until they reconnect.
    private val suppressReconnect = ConcurrentHashMap.newKeySet<String>()
    private val discovery = DeviceDiscovery(self, ::onBeacon)
    private var serverSocket: ServerSocket? = null

    // Callbacks the UI/service subscribe to.
    var onDevicesChanged: (() -> Unit)? = null
    var onPeerConnected: ((DeviceInfo) -> Unit)? = null
    var onPeerDisconnected: ((DeviceInfo) -> Unit)? = null
    var onPacket: ((DeviceInfo, Packet) -> Unit)? = null
    /** Return true to accept a pairing request. Defaults to accepting (UI shows the code). */
    var onPairingRequest: ((DeviceInfo, String) -> Boolean)? = null

    val knownDevices: List<DeviceInfo> get() = known.values.toList()

    fun start() {
        val server = ServerSocket().apply {
            reuseAddress = true
            bind(InetSocketAddress(Ports.TCP))
        }
        serverSocket = server
        scope.launch { acceptLoop(server) }
        discovery.start()
        scope.launch { heartbeatLoop() }
        log.i("Conduit node '${self.name}' started on TCP ${Ports.TCP}")
    }

    // ---- Incoming connections -------------------------------------------------

    private fun acceptLoop(server: ServerSocket) {
        while (scope.isActive) {
            try {
                val client = server.accept()
                log.d("Incoming TCP from ${client.inetAddress?.hostAddress}")
                handleConnection(client)
            } catch (e: Exception) {
                if (scope.isActive) log.w(e, "Accept loop error")
            }
        }
    }

    private fun handleConnection(socket: Socket) {
        val conn = PeerConnection(socket, crypto, self)
        wire(conn)
        conn.start()
    }

    // ---- Outgoing connections -------------------------------------------------

    private fun onBeacon(device: DeviceInfo) {
        val isNew = !known.containsKey(device.deviceId)
        known.compute(device.deviceId) { _, existing ->
            (existing ?: device).apply {
                name = device.name
                ipAddress = device.ipAddress
                tcpPort = device.tcpPort
                lastSeen = System.currentTimeMillis()
                isPaired = store.isPaired(device.deviceId)
            }
        }
        if (isNew) {
            log.i("Discovered ${device.name}")
            onDevicesChanged?.invoke()
        }
        if (store.isPaired(device.deviceId) && !peers.containsKey(device.deviceId) && device.ipAddress != null &&
            !suppressReconnect.contains(device.deviceId)
        ) {
            connect(device)
        }
    }

    fun connect(device: DeviceInfo) {
        // An explicit connect clears any manual-disconnect suppression.
        suppressReconnect.remove(device.deviceId)
        if (peers.containsKey(device.deviceId) || device.ipAddress == null) return
        scope.launch {
            try {
                log.i("Connecting to ${device.name} @ ${device.ipAddress}:${device.tcpPort}")
                val socket = Socket()
                socket.connect(InetSocketAddress(device.ipAddress, device.tcpPort), 5000)
                val conn = PeerConnection(socket, crypto, self)
                wire(conn)
                conn.start()
            } catch (e: Exception) {
                log.w(e, "Failed to connect to ${device.name}")
            }
        }
    }

    private fun wire(conn: PeerConnection) {
        conn.onHandshaked = { peer ->
            // A completed session means someone reconnected on purpose — allow auto-reconnect again.
            suppressReconnect.remove(peer.deviceId)
            peer.isPaired = store.isPaired(peer.deviceId)
            peers[peer.deviceId] = conn
            known[peer.deviceId] = peer
            log.i("Peer connected: ${peer.name} (paired=${peer.isPaired})")
            onPeerConnected?.invoke(peer)
            onDevicesChanged?.invoke()
        }
        conn.onPacket = { packet -> conn.peer?.let { onIncoming(conn, it, packet) } }
        conn.onDisconnected = {
            conn.peer?.let { p ->
                peers.remove(p.deviceId)
                onPeerDisconnected?.invoke(p)
                onDevicesChanged?.invoke()
            }
        }
    }

    private fun onIncoming(conn: PeerConnection, peer: DeviceInfo, packet: Packet) {
        when (packet.type) {
            PacketType.PAIR_REQUEST -> handlePairRequest(conn, peer, packet)
            PacketType.PAIR_RESPONSE -> handlePairResponse(peer, packet)
            PacketType.DISCONNECT -> {
                log.i("${peer.name} disconnected")
                suppressReconnect.add(peer.deviceId)
                peers.remove(peer.deviceId)
                onPeerDisconnected?.invoke(peer)
                onDevicesChanged?.invoke()
                conn.close()
            }
            else -> {
                // Security gate: only paired peers may use features. An unpaired peer can still
                // complete the handshake and exchange pair-request/response (handled above), but
                // every feature packet is dropped until it's actually paired.
                if (!store.isPaired(peer.deviceId)) {
                    log.w("Dropping ${packet.type} from unpaired peer ${peer.name}")
                    return
                }
                onPacket?.invoke(peer, packet)
            }
        }
    }

    // ---- Pairing --------------------------------------------------------------

    private fun handlePairRequest(conn: PeerConnection, peer: DeviceInfo, packet: Packet) {
        val code = packet.getString("code") ?: "------"
        val publicKey = packet.getString("publicKey") ?: ""
        log.i("Pair request from ${peer.name}, code $code")

        // If the UI provides a decision callback, honour it; otherwise accept by default
        // (the UI still shows the code so the user can verify the peer).
        val accepted = onPairingRequest?.invoke(peer, code) ?: true
        if (accepted && publicKey.isNotEmpty()) {
            store.addPaired(PairedDevice(peer.deviceId, peer.name, peer.type, publicKey))
            peer.isPaired = true
        }
        conn.send(Packet.create(PacketType.PAIR_RESPONSE) {
            put("accepted", accepted)
            put("publicKey", crypto.publicKeyBase64)
        })
        onDevicesChanged?.invoke()
    }

    private fun handlePairResponse(peer: DeviceInfo, packet: Packet) {
        val accepted = packet.getBool("accepted")
        val publicKey = packet.getString("publicKey") ?: ""
        log.i("Pair response from ${peer.name}: accepted=$accepted")
        if (accepted && publicKey.isNotEmpty()) {
            store.addPaired(PairedDevice(peer.deviceId, peer.name, peer.type, publicKey))
            peer.isPaired = true
            onDevicesChanged?.invoke()
        }
    }

    /** Begin pairing with a discovered device; returns the 6-digit code to show the user. */
    suspend fun startPairing(device: DeviceInfo): String {
        if (!peers.containsKey(device.deviceId)) {
            connect(device)
            delay(600)
        }
        val conn = peers[device.deviceId] ?: throw IllegalStateException("Not connected to device")
        val code = Random.nextInt(0, 1_000_000).toString().padStart(6, '0')
        conn.send(Packet.create(PacketType.PAIR_REQUEST) {
            put("publicKey", crypto.publicKeyBase64)
            put("code", code)
        })
        log.i("Started pairing with ${device.name}, code $code")
        return code
    }

    // ---- Sending --------------------------------------------------------------

    fun isConnected(deviceId: String) = peers.containsKey(deviceId)

    /**
     * Drop the live session with a device and stop auto-reconnecting to it until the user
     * explicitly connects again (or the app restarts).
     */
    fun disconnect(deviceId: String) {
        suppressReconnect.add(deviceId)
        // Drop the peer from the connected set immediately so the UI flips to "offline"
        // right away, then send the farewell + close the socket in the background.
        val conn = peers.remove(deviceId)
        if (conn != null) {
            log.i("Disconnecting from $deviceId (manual)")
            known[deviceId]?.let { onPeerDisconnected?.invoke(it) }
            onDevicesChanged?.invoke()
            // Tell the peer so it also stops auto-reconnecting, then close.
            conn.closeWith(Packet.create(PacketType.DISCONNECT))
        }
    }

    fun sendTo(deviceId: String, packet: Packet): Boolean {
        val conn = peers[deviceId]
        if (conn != null) {
            conn.send(packet)
            return true
        }
        log.d("No active connection to $deviceId; dropping ${packet.type}")
        return false
    }

    fun broadcast(packet: Packet) = peers.keys.forEach { sendTo(it, packet) }

    private suspend fun heartbeatLoop() {
        while (scope.isActive) {
            delay(15_000)
            peers.keys.forEach { sendTo(it, Packet.create(PacketType.PING)) }
        }
    }

    fun stop() {
        scope.cancel()
        peers.values.forEach { it.close() }
        discovery.stop()
        runCatching { serverSocket?.close() }
        log.i("Conduit node stopped")
    }
}
