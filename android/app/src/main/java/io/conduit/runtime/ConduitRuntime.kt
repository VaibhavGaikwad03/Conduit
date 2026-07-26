package io.conduit.runtime

import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * Process-wide handle the UI reads and the service populates. Keeps the Compose screens
 * decoupled from the foreground service lifecycle.
 */
object ConduitRuntime {
    @Volatile var node: ConduitNode? = null

    val devices = MutableStateFlow<List<DeviceInfo>>(emptyList())
    val connectedCount = MutableStateFlow(0)
    val lastEvent = MutableStateFlow("")

    fun refreshDevices() {
        val n = node ?: return
        devices.value = n.knownDevices.sortedByDescending { it.lastSeen }
        connectedCount.value = n.knownDevices.count { n.isConnected(it.deviceId) }
    }
}
