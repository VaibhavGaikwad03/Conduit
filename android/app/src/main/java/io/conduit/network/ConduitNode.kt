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

    // Devices with an outbound dial already in flight — stops a second beacon from
    // opening a duplicate connection before the first has finished its handshake.
    private val connecting = ConcurrentHashMap.newKeySet<String>()
    private val discovery = DeviceDiscovery(self, ::onBeacon)
    private var serverSocket: ServerSocket? = null

    // Callbacks the UI/service subscribe to.
    var onDevicesChanged: (() -> Unit)? = null
    var onPeerConnected: ((DeviceInfo) -> Unit)? = null
    var onPeerDisconnected: ((DeviceInfo) -> Unit)? = null
    var onPacket: ((DeviceInfo, Packet) -> Unit)? = null
    /** Return true to accept a pairing request. Defaults to accepting (UI shows the code). */
    // The UI shows a confirm/reject dialog, then calls the provided respond() with the decision.
    var onPairingRequest: ((peer: DeviceInfo, code: String, respond: (Boolean) -> Unit) -> Unit)? = null

    val knownDevices: List<DeviceInfo> get() = known.values.toList()

    fun start() {
        // Surface remembered peers up front (as offline) so the user can see and manage them
        // before any beacon arrives — including stale ones left by a peer reinstall.
        for (p in store.pairedDevices()) {
            known.putIfAbsent(
                p.deviceId,
                DeviceInfo(deviceId = p.deviceId, name = p.name, type = p.type, isPaired = true),
            )
        }

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
        // Only one side dials (the smaller deviceId); the other only accepts. Both sides
        // auto-dialing races to open two sockets at once and can "glare" — each keeps a
        // different one and closes the other — which churns the session and breaks
        // transfers. Explicit/pairing connects bypass this.
        if (store.isPaired(device.deviceId) && !peers.containsKey(device.deviceId) && device.ipAddress != null &&
            !suppressReconnect.contains(device.deviceId) &&
            self.deviceId < device.deviceId
        ) {
            connect(device)
        }
    }

    fun connect(device: DeviceInfo) {
        // An explicit connect clears any manual-disconnect suppression.
        suppressReconnect.remove(device.deviceId)
        if (peers.containsKey(device.deviceId) || device.ipAddress == null) return
        // Coalesce concurrent dials: if one is already in flight for this device, skip.
        if (!connecting.add(device.deviceId)) return
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
            } finally {
                connecting.remove(device.deviceId)
            }
        }
    }

    private fun wire(conn: PeerConnection) {
        conn.onHandshaked = { peer ->
            // A completed session means someone reconnected on purpose — allow auto-reconnect again.
            suppressReconnect.remove(peer.deviceId)
            peer.isPaired = store.isPaired(peer.deviceId)

            // Keep exactly one live session per peer. A burst of duplicate connections
            // (both sides dialing, or several beacons before the first handshake lands)
            // would otherwise let a single file's packets race across sockets and truncate
            // session-based transfers. Adopt the first connection to arrive and drop any
            // later duplicate — never the established one, which may be mid-transfer.
            val existing = peers.putIfAbsent(peer.deviceId, conn)
            if (existing != null && existing !== conn) {
                log.i("Duplicate session with ${peer.name}; dropping the redundant connection")
                conn.close()
            } else {
                known[peer.deviceId] = peer
                log.i("Peer connected: ${peer.name} (paired=${peer.isPaired})")
                onPeerConnected?.invoke(peer)
                onDevicesChanged?.invoke()
            }
        }
        conn.onPacket = { packet -> conn.peer?.let { onIncoming(conn, it, packet) } }
        conn.onDisconnected = {
            conn.peer?.let { p ->
                // Only the currently-registered connection owns the peer entry. A superseded
                // duplicate closing must not evict the live session or fire "disconnected".
                if (peers.remove(p.deviceId, conn)) {
                    onPeerDisconnected?.invoke(p)
                    onDevicesChanged?.invoke()
                }
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

        // Ask the user (via the UI) to confirm the code before accepting. respond() runs when
        // they answer; only then do we trust the peer and reply. No UI wired → reject (the code
        // must be confirmed by a human — never silently auto-accept).
        val respond: (Boolean) -> Unit = { accepted ->
            scope.launch {
                if (accepted && publicKey.isNotEmpty()) {
                    store.addPaired(PairedDevice(peer.deviceId, peer.name, peer.type, publicKey))
                    peer.isPaired = true
                }
                val response = Packet.create(PacketType.PAIR_RESPONSE) {
                    put("accepted", accepted)
                    put("publicKey", crypto.publicKeyBase64)
                }
                if (accepted) {
                    conn.send(response)
                    onDevicesChanged?.invoke()
                } else {
                    // Reject: send the refusal, then drop the session so it doesn't linger as "connected".
                    conn.closeWith(response)
                }
                log.i("Pair request from ${peer.name} ${if (accepted) "accepted" else "rejected"}")
            }
        }
        val handler = onPairingRequest
        if (handler != null) handler(peer, code, respond) else respond(false)
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

    /** The last-known IP of a device, for opening a side channel (e.g. the file stream). */
    fun ipFor(deviceId: String): String? = known[deviceId]?.ipAddress

    /**
     * The AES-256 session key shared with a paired peer, derived from its stored public key.
     * Deterministic (ECDH), so the file-stream side channel encrypts with the same key the main
     * session uses, without needing the live connection.
     */
    fun sessionKeyFor(deviceId: String): ByteArray? {
        val pub = store.getPaired(deviceId)?.publicKey ?: return null
        return try { crypto.deriveSessionKey(pub) } catch (e: Exception) { null }
    }

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

    /**
     * Forget a device completely: drop any live session, delete the stored pairing, and remove
     * it from the device list. Clears out stale entries (e.g. a peer reinstalled under a new
     * identity, leaving the old one orphaned).
     */
    fun forget(deviceId: String) {
        disconnect(deviceId)
        store.removePaired(deviceId)
        known.remove(deviceId)
        suppressReconnect.remove(deviceId)
        log.i("Forgot device $deviceId")
        onDevicesChanged?.invoke()
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
