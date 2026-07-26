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

    /** Active file transfers, shown with a progress bar in the UI. */
    val transfers = MutableStateFlow<List<TransferUi>>(emptyList())

    @Synchronized
    fun upsertTransfer(t: TransferUi) {
        val list = transfers.value.toMutableList()
        val idx = list.indexOfFirst { it.id == t.id }
        if (idx >= 0) list[idx] = t else list.add(t)
        transfers.value = list
    }

    @Synchronized
    fun removeTransfer(id: String) {
        transfers.value = transfers.value.filter { it.id != id }
    }

    fun refreshDevices() {
        val n = node ?: return
        devices.value = n.knownDevices.sortedByDescending { it.lastSeen }
        connectedCount.value = n.knownDevices.count { n.isConnected(it.deviceId) }
    }
}

/** One file transfer's progress, for the UI. */
data class TransferUi(
    val id: String,
    val name: String,
    val sending: Boolean,
    val transferred: Long,
    val total: Long,
    val done: Boolean = false,
    val failed: Boolean = false,
) {
    val percent: Int
        get() = when {
            total > 0 -> ((transferred * 100) / total).toInt().coerceIn(0, 100)
            done -> 100
            else -> 0
        }
}
