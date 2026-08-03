package io.conduit.model

enum class DeviceType { UNKNOWN, ANDROID, WINDOWS }

enum class ConnectionState { DISCONNECTED, DISCOVERED, CONNECTING, CONNECTED, PAIRED }

/** A device seen on the network. */
data class DeviceInfo(
    val deviceId: String,
    var name: String,
    var type: DeviceType = DeviceType.UNKNOWN,
    var ipAddress: String? = null,
    var tcpPort: Int = Ports.TCP,
    var protocol: Int = 1,
    var lastSeen: Long = System.currentTimeMillis(),
    var state: ConnectionState = ConnectionState.DISCOVERED,
    var isPaired: Boolean = false,
)

/** A remembered, trusted peer. */
data class PairedDevice(
    val deviceId: String,
    val name: String,
    val type: DeviceType,
    val publicKey: String,
    val pairedAt: Long = System.currentTimeMillis(),
)

object Ports {
    const val UDP = 5461
    const val TCP = 5462
    const val WEBCAM = 5463
    const val SCREEN = 5464
    const val FILE_STREAM = 5465 // raw, encrypted bulk file transfer (fast path for big files)
    const val PROTOCOL_VERSION = 1
}
