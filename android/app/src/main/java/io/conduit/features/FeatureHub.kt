package io.conduit.features

import android.content.Context
import android.content.Intent
import android.net.Uri
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.BrowseEntryUi
import io.conduit.runtime.ConduitRuntime
import io.conduit.runtime.SearchResultUi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * Routes every incoming packet to the right Android feature and exposes helpers the
 * features use to push data back to the PC. Mirrors the Windows FeatureCoordinator.
 */
class FeatureHub(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("Features")

    val clipboard = ClipboardFeature(context)
    val media = MediaFeature(context)
    val mediaState = MediaStateFeature(context, node)
    val files = FileFeature(context, node)
    val fileStream = FileStreamFeature(context, node)
    val battery = BatteryFeature(context, node)
    val status = DeviceStatusFeature(context, node)
    val remote = RemoteCommandFeature(context)
    val sms = SmsFeature(context, node)
    val webcam = WebcamStreamer(context)
    val screen = ScreenStreamer(context)
    val fileSearch = FileSearchFeature(context)

    // Searches we're serving for the peer run off the read loop so a cancel can abort them mid-walk.
    private val searchScope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val cancelledSearches = ConcurrentHashMap.newKeySet<String>()

    fun start() {
        node.onPacket = { peer, packet -> handle(peer, packet) }
        files.stream = fileStream
        fileStream.start()
        battery.start()
        status.start()
        mediaState.start()
        clipboard.onLocalChange = { text ->
            node.broadcast(Packet.create(PacketType.CLIPBOARD) {
                put("content", text); put("contentType", "text")
            })
        }
        clipboard.start()
        log.i("Feature hub started")
    }

    fun stop() {
        battery.stop()
        status.stop()
        mediaState.stop()
        clipboard.stop()
        webcam.stop()
        screen.stop()
        fileStream.stop()
    }

    private fun handle(peer: DeviceInfo, packet: Packet) {
        try {
            when (packet.type) {
                PacketType.CLIPBOARD -> clipboard.setFromRemote(packet.getString("content") ?: "")
                PacketType.MEDIA_COMMAND -> media.handle(packet.getString("command") ?: "", packet.getDouble("value"))
                PacketType.REMOTE_COMMAND -> remote.handle(packet.getString("command") ?: "")
                PacketType.FILE_OFFER ->
                    // A stream offer means the bytes arrive over the raw fast port, not as chunks here.
                    if (packet.getBool("stream")) {
                        fileStream.registerIncoming(
                            packet.getString("transferId") ?: "", peer.deviceId,
                            packet.getString("name") ?: "conduit-file", packet.getLong("size"),
                        )
                    } else {
                        files.handle(packet)
                    }
                PacketType.FILE_CHUNK, PacketType.FILE_COMPLETE -> files.handle(packet)
                PacketType.NOTIFICATION_ACTION -> ConduitNotificationListener.instance?.handleAction(packet)
                PacketType.SMS_SEND -> sms.send(packet.getString("address") ?: "", packet.getString("body") ?: "")
                PacketType.SMS_LIST -> sms.sendThreadList()
                PacketType.WEBCAM_START -> peer.ipAddress?.let { ip ->
                    webcam.start(ip, packet.getInt("port", 5463), packet.getString("facing") ?: "front")
                }
                PacketType.WEBCAM_STOP -> webcam.stop()
                PacketType.WEBCAM_SWITCH -> webcam.switchCamera(packet.getString("facing") ?: "front")
                PacketType.SCREEN_START -> peer.ipAddress?.let { ip ->
                    screen.prepare(ip, packet.getInt("port", 5464))
                    ScreenCaptureActivity.promptForCapture(context) // asks the user for capture consent
                }
                PacketType.SCREEN_STOP -> screen.stop()
                PacketType.INPUT -> handleInput(packet)
                PacketType.FILE_SEARCH -> handleFileSearch(peer, packet)
                PacketType.FILE_SEARCH_RESULT -> handleFileSearchResult(peer, packet)
                PacketType.FILE_SEARCH_CANCEL -> handleFileSearchCancel(packet)
                PacketType.FILE_REQUEST -> {
                    val uri = fileSearch.resolve(packet.getString("id") ?: "")
                    if (uri != null) files.sendFile(peer.deviceId, uri)
                    else log.w("file-request for unknown id ignored")
                }
                PacketType.DIR_LIST -> handleDirList(peer, packet)
                PacketType.DIR_LIST_RESULT -> handleDirListResult(packet)
                PacketType.OPEN_LINK -> openLink(packet.getString("url") ?: "")
                else -> log.d("Unhandled packet type ${packet.type}")
            }
        } catch (e: Exception) {
            log.e(e, "Error handling ${packet.type} from ${peer.name}")
        }
    }

    /** Route a remote-input packet to the accessibility service, prompting to enable it if needed. */
    private fun handleInput(packet: Packet) {
        val svc = ConduitInputService.instance
        if (svc == null) {
            ConduitInputService.promptEnable(context)
            return
        }
        when (packet.getString("action")) {
            "tap" -> svc.tap(fx(packet, "x"), fx(packet, "y"))
            "swipe" -> svc.swipe(
                fx(packet, "x"), fx(packet, "y"), fx(packet, "x2"), fx(packet, "y2"),
                packet.getLong("durationMs", 120),
            )
            "key" -> svc.key(packet.getString("key"))
            "text" -> svc.typeText(packet.getString("text") ?: "")
            else -> log.d("Unknown input action")
        }
    }

    private fun fx(packet: Packet, key: String): Float =
        (packet.getDouble(key) ?: 0.0).toFloat().coerceIn(0f, 1f)

    /** Open a peer-supplied URL in the browser (http/https only). */
    private fun openLink(rawUrl: String) {
        val url = normalizeUrl(rawUrl) ?: run { log.w("Ignoring non-http open-link: $rawUrl"); return }
        try {
            context.startActivity(
                Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
            log.i("Opened link $url")
        } catch (e: Exception) {
            log.w(e, "Failed to open link")
        }
    }

    /** Trims, adds https:// when no scheme, and only allows http/https. Null if invalid. */
    private fun normalizeUrl(raw: String): String? {
        val s = raw.trim()
        if (s.isEmpty()) return null
        val withScheme = if (!s.contains("://")) "https://$s" else s
        return when (Uri.parse(withScheme).scheme?.lowercase()) {
            "http", "https" -> withScheme
            else -> null
        }
    }

    /** Peer asked us to search our files: run it off the read loop and reply with the matches. */
    private fun handleFileSearch(peer: DeviceInfo, packet: Packet) {
        val requestId = packet.getString("requestId") ?: ""
        val query = packet.getString("query") ?: ""
        cancelledSearches.remove(requestId) // clear any stale flag before starting
        searchScope.launch {
            val (results, truncated) = fileSearch.search(query) { cancelledSearches.contains(requestId) }
            if (cancelledSearches.remove(requestId)) return@launch // stopped by the peer — don't reply
            node.sendTo(peer.deviceId, Packet.create(PacketType.FILE_SEARCH_RESULT) {
                put("requestId", requestId)
                put("truncated", truncated)
                put("results", JSONArray().apply {
                    results.forEach { r ->
                        put(JSONObject().apply {
                            put("id", r.id); put("name", r.name); put("size", r.size)
                            put("folder", r.folder); put("mime", r.mime)
                        })
                    }
                })
            })
        }
    }

    /** Peer asked us to stop a search it started — abort the matching in-flight walk. */
    private fun handleFileSearchCancel(packet: Packet) {
        val requestId = packet.getString("requestId") ?: ""
        if (requestId.isNotEmpty()) {
            cancelledSearches.add(requestId)
            log.i("File search $requestId cancelled by peer")
        }
    }

    /** Peer asked us to list a folder — walk it off the read loop and reply with the entries. */
    private fun handleDirList(peer: DeviceInfo, packet: Packet) {
        val requestId = packet.getString("requestId") ?: ""
        val token = packet.getString("token") ?: ""
        searchScope.launch {
            val listing = fileSearch.listDir(token)
            node.sendTo(peer.deviceId, Packet.create(PacketType.DIR_LIST_RESULT) {
                put("requestId", requestId)
                put("token", token)
                put("name", listing.name)
                put("path", listing.path)
                listing.parent?.let { put("parent", it) }
                listing.error?.let { put("error", it) }
                put("entries", JSONArray().apply {
                    listing.entries.forEach { e ->
                        put(JSONObject().apply {
                            put("name", e.name); put("isDir", e.isDir)
                            put("token", e.token); put("size", e.size); put("mime", e.mime)
                        })
                    }
                })
            })
        }
    }

    /** Peer replied to our browse request: surface the folder listing to the UI. */
    private fun handleDirListResult(packet: Packet) {
        val arr = packet.body.optJSONArray("entries")
        val list = mutableListOf<BrowseEntryUi>()
        if (arr != null) {
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                list.add(
                    BrowseEntryUi(
                        token = o.optString("token"),
                        name = o.optString("name"),
                        isDir = o.optBoolean("isDir"),
                        size = o.optLong("size"),
                        mime = o.optString("mime"),
                    ),
                )
            }
        }
        ConduitRuntime.setDirListing(
            requestId = packet.getString("requestId") ?: "",
            path = packet.getString("path") ?: "",
            parent = packet.getString("parent"),
            error = packet.getString("error"),
            entries = list,
        )
    }

    /** Peer replied to our search: surface the matches to the UI. */
    private fun handleFileSearchResult(peer: DeviceInfo, packet: Packet) {
        val arr = packet.body.optJSONArray("results")
        val list = mutableListOf<SearchResultUi>()
        if (arr != null) {
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                list.add(
                    SearchResultUi(
                        id = o.optString("id"),
                        name = o.optString("name"),
                        size = o.optLong("size"),
                        folder = o.optString("folder"),
                        mime = o.optString("mime"),
                        deviceId = peer.deviceId,
                    ),
                )
            }
        }
        ConduitRuntime.setSearchResults(packet.getString("requestId") ?: "", list, packet.getBool("truncated"))
    }

    /** Push current battery + device status + now-playing to all peers (called on connect). */
    fun pushStatus() {
        battery.sendNow()
        status.sendNow()
        mediaState.sendNow()
    }
}
