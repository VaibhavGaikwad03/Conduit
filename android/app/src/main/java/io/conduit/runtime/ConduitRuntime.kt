package io.conduit.runtime

import io.conduit.features.FileFeature
import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * Process-wide handle the UI reads and the service populates. Keeps the Compose screens
 * decoupled from the foreground service lifecycle.
 */
object ConduitRuntime {
    @Volatile var node: ConduitNode? = null

    /** Set by the service; lets the UI start an outgoing (phone → PC) file transfer. */
    @Volatile var files: FileFeature? = null

    val devices = MutableStateFlow<List<DeviceInfo>>(emptyList())
    val connectedCount = MutableStateFlow(0)
    /** Ids of currently-connected peers — an observable source the UI can react to. */
    val connectedIds = MutableStateFlow<Set<String>>(emptySet())
    val lastEvent = MutableStateFlow("")

    /** A pending incoming pair request awaiting the user's confirm/reject. Null when none. */
    val pendingPairing = MutableStateFlow<PairingPrompt?>(null)

    /** Surface an incoming pair request to the UI; [respond] sends the accept/reject decision. */
    fun requestPairing(deviceName: String, code: String, respond: (Boolean) -> Unit) {
        pendingPairing.value = PairingPrompt(deviceName, code, respond)
    }

    /** The user answered the pairing dialog. */
    fun answerPairing(accept: Boolean) {
        val prompt = pendingPairing.value ?: return
        pendingPairing.value = null
        prompt.respond(accept)
    }

    /** Active file transfers, shown with a progress bar in the UI. */
    val transfers = MutableStateFlow<List<TransferUi>>(emptyList())

    /** Results from the last cross-device file search (the peer's matching files). */
    val searchResults = MutableStateFlow<List<SearchResultUi>>(emptyList())
    val searchTruncated = MutableStateFlow(false)
    /** True while a search request is outstanding, so the UI can show a spinner/hint. */
    val searchPending = MutableStateFlow(false)

    fun setSearchResults(list: List<SearchResultUi>, truncated: Boolean) {
        searchResults.value = list
        searchTruncated.value = truncated
        searchPending.value = false
    }

    fun beginSearch() {
        searchResults.value = emptyList()
        searchTruncated.value = false
        searchPending.value = true
    }

    /** Clears the current search results and status; called when the user closes the search list. */
    fun clearSearch() {
        searchResults.value = emptyList()
        searchTruncated.value = false
        searchPending.value = false
    }

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
        val connected = n.knownDevices.filter { n.isConnected(it.deviceId) }.map { it.deviceId }.toSet()
        connectedIds.value = connected
        connectedCount.value = connected.size
    }
}

/** An incoming pair request the user must confirm: shows the peer name + 6-digit code. */
data class PairingPrompt(
    val deviceName: String,
    val code: String,
    val respond: (Boolean) -> Unit,
)

/** One file found on the connected peer, shown in the search results list. */
data class SearchResultUi(
    val id: String,
    val name: String,
    val size: Long,
    val folder: String,
    val mime: String,
    val deviceId: String,
)

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
