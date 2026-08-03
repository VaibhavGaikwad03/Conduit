package io.conduit.runtime

import io.conduit.features.FileFeature
import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import kotlinx.coroutines.flow.MutableStateFlow
import java.util.UUID

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

    // The in-flight search's id and the peer serving it, so we can cancel it and drop stale replies.
    @Volatile private var activeSearchId: String? = null
    @Volatile private var activeSearchDeviceId: String? = null

    fun setSearchResults(requestId: String, list: List<SearchResultUi>, truncated: Boolean) {
        if (requestId != activeSearchId) return // stale/cancelled reply — ignore
        searchResults.value = list
        searchTruncated.value = truncated
        searchPending.value = false
    }

    fun beginSearch(requestId: String, deviceId: String) {
        activeSearchId = requestId
        activeSearchDeviceId = deviceId
        searchResults.value = emptyList()
        searchTruncated.value = false
        searchPending.value = true
    }

    /** Stops the current search: tells the peer to abort its walk, then clears local state. */
    fun cancelSearch() {
        val id = activeSearchId
        val deviceId = activeSearchDeviceId
        activeSearchId = null
        activeSearchDeviceId = null
        if (id != null && deviceId != null) {
            node?.sendTo(deviceId, Packet.create(PacketType.FILE_SEARCH_CANCEL) { put("requestId", id) })
        }
        searchResults.value = emptyList()
        searchTruncated.value = false
        searchPending.value = false
    }

    // ---- Remote file browser (this phone browsing the PC's folders) ----
    val browseEntries = MutableStateFlow<List<BrowseEntryUi>>(emptyList())
    val browseStatus = MutableStateFlow("")
    val browsePath = MutableStateFlow("")
    val browseActive = MutableStateFlow(false)
    val browseCanGoUp = MutableStateFlow(false)

    // Guards stale replies; the parent token comes from the last reply (null = at the top level).
    @Volatile private var activeBrowseId: String? = null
    @Volatile private var browseParent: String? = null

    /** Opens the browser at the default landing folder; returns the request id (send an empty token). */
    @Synchronized
    fun startBrowse(): String {
        val id = UUID.randomUUID().toString().replace("-", "")
        activeBrowseId = id
        browseParent = null
        browseActive.value = true
        browseEntries.value = emptyList()
        browseStatus.value = "Loading…"
        browsePath.value = ""
        browseCanGoUp.value = false
        return id
    }

    /** Starts a navigation (folder open or up); returns a fresh request id to send with the token. */
    @Synchronized
    fun navigate(): String {
        val id = UUID.randomUUID().toString().replace("-", "")
        activeBrowseId = id
        browseStatus.value = "Loading…"
        return id
    }

    /** Goes up one level; returns the request id and the parent token, or null if already at the top. */
    @Synchronized
    fun browseUp(): Pair<String, String>? {
        val parent = browseParent ?: return null
        return navigate() to parent
    }

    /** Closes the browser and clears its state. */
    @Synchronized
    fun closeBrowse() {
        activeBrowseId = null
        browseParent = null
        browseActive.value = false
        browseEntries.value = emptyList()
        browseStatus.value = ""
        browsePath.value = ""
        browseCanGoUp.value = false
    }

    @Synchronized
    fun setDirListing(requestId: String, path: String, parent: String?, error: String?, entries: List<BrowseEntryUi>) {
        if (requestId != activeBrowseId) return // stale/superseded reply — ignore
        browseParent = parent
        browseCanGoUp.value = parent != null
        browsePath.value = path
        if (error != null) {
            browseEntries.value = emptyList()
            browseStatus.value = error
            return
        }
        browseEntries.value = entries
        val folders = entries.count { it.isDir }
        val files = entries.size - folders
        browseStatus.value = if (entries.isEmpty()) "Empty folder"
        else "$folders folder${if (folders == 1) "" else "s"}, $files file${if (files == 1) "" else "s"}"
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

/** One entry (folder or file) in the remote file browser. */
data class BrowseEntryUi(
    val token: String,
    val name: String,
    val isDir: Boolean,
    val size: Long,
    val mime: String,
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
