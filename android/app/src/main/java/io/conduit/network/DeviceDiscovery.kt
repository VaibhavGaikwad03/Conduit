package io.conduit.network

import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.model.DeviceType
import io.conduit.model.Ports
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

/**
 * UDP presence layer: broadcasts this device's identity beacon on port 5461 every 3s and
 * listens for beacons from other devices. See PROTOCOL.md §1.
 */
class DeviceDiscovery(
    private val self: DeviceInfo,
    private val onBeacon: (DeviceInfo) -> Unit,
) {
    private val log = ConduitLog.tag("Discovery")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var socket: DatagramSocket? = null

    fun start() {
        val sock = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(Ports.UDP))
        }
        socket = sock
        scope.launch { listenLoop(sock) }
        scope.launch { broadcastLoop(sock) }
        log.i("Discovery started on UDP ${Ports.UDP}")
    }

    private suspend fun broadcastLoop(sock: DatagramSocket) {
        while (scope.isActive) {
            announce(sock)
            delay(3000)
        }
    }

    fun announce(sock: DatagramSocket? = socket) {
        sock ?: return
        try {
            val beacon = JSONObject().apply {
                put("conduit", 1)
                put("deviceId", self.deviceId)
                put("name", self.name)
                put("type", "android")
                put("tcpPort", self.tcpPort)
                put("protocol", Ports.PROTOCOL_VERSION)
            }
            val data = beacon.toString().toByteArray()
            val packet = DatagramPacket(
                data, data.size, InetAddress.getByName("255.255.255.255"), Ports.UDP,
            )
            sock.send(packet)
        } catch (e: Exception) {
            log.w(e, "Failed to send beacon")
        }
    }

    private fun listenLoop(sock: DatagramSocket) {
        val buf = ByteArray(2048)
        while (scope.isActive) {
            try {
                val packet = DatagramPacket(buf, buf.size)
                sock.receive(packet)
                handleBeacon(String(packet.data, 0, packet.length), packet.address.hostAddress)
            } catch (e: Exception) {
                if (scope.isActive) log.w(e, "Error receiving beacon")
            }
        }
    }

    private fun handleBeacon(json: String, fromIp: String?) {
        try {
            val obj = JSONObject(json)
            if (!obj.has("conduit")) return
            val deviceId = obj.optString("deviceId")
            if (deviceId.isEmpty() || deviceId == self.deviceId) return

            val device = DeviceInfo(
                deviceId = deviceId,
                name = obj.optString("name", "Unknown"),
                type = if (obj.optString("type") == "windows") DeviceType.WINDOWS else DeviceType.ANDROID,
                ipAddress = fromIp,
                tcpPort = obj.optInt("tcpPort", Ports.TCP),
                protocol = obj.optInt("protocol", 1),
            )
            onBeacon(device)
        } catch (e: Exception) {
            log.v(e, "Ignoring malformed beacon")
        }
    }

    fun stop() {
        scope.cancel()
        socket?.close()
        log.i("Discovery stopped")
    }
}
