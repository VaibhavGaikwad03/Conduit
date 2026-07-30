package io.conduit.features

import android.content.Context
import android.content.Intent
import android.net.Uri
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime
import io.conduit.runtime.SearchResultUi
import org.json.JSONArray
import org.json.JSONObject

/**
 * Routes every incoming packet to the right Android feature and exposes helpers the
 * features use to push data back to the PC. Mirrors the Windows FeatureCoordinator.
 */
class FeatureHub(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("Features")

    val clipboard = ClipboardFeature(context)
    val media = MediaFeature(context)
    val files = FileFeature(context, node)
    val battery = BatteryFeature(context, node)
    val status = DeviceStatusFeature(context, node)
    val remote = RemoteCommandFeature(context)
    val sms = SmsFeature(context, node)
    val webcam = WebcamStreamer(context)
    val fileSearch = FileSearchFeature(context)

    fun start() {
        node.onPacket = { peer, packet -> handle(peer, packet) }
        battery.start()
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
        clipboard.stop()
        webcam.stop()
    }

    private fun handle(peer: DeviceInfo, packet: Packet) {
        try {
            when (packet.type) {
                PacketType.CLIPBOARD -> clipboard.setFromRemote(packet.getString("content") ?: "")
                PacketType.MEDIA_COMMAND -> media.handle(packet.getString("command") ?: "", packet.getDouble("value"))
                PacketType.REMOTE_COMMAND -> remote.handle(packet.getString("command") ?: "")
                PacketType.FILE_OFFER, PacketType.FILE_CHUNK, PacketType.FILE_COMPLETE -> files.handle(packet)
                PacketType.NOTIFICATION_ACTION -> ConduitNotificationListener.instance?.handleAction(packet)
                PacketType.SMS_SEND -> sms.send(packet.getString("address") ?: "", packet.getString("body") ?: "")
                PacketType.SMS_LIST -> sms.sendThreadList()
                PacketType.WEBCAM_START -> peer.ipAddress?.let { ip -> webcam.start(ip, packet.getInt("port", 5463)) }
                PacketType.WEBCAM_STOP -> webcam.stop()
                PacketType.FILE_SEARCH -> handleFileSearch(peer, packet)
                PacketType.FILE_SEARCH_RESULT -> handleFileSearchResult(peer, packet)
                PacketType.FILE_REQUEST -> {
                    val uri = fileSearch.resolve(packet.getString("id") ?: "")
                    if (uri != null) files.sendFile(peer.deviceId, uri)
                    else log.w("file-request for unknown id ignored")
                }
                PacketType.OPEN_LINK -> openLink(packet.getString("url") ?: "")
                else -> log.d("Unhandled packet type ${packet.type}")
            }
        } catch (e: Exception) {
            log.e(e, "Error handling ${packet.type} from ${peer.name}")
        }
    }

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

    /** Peer asked us to search our files: run it and reply with the matches. */
    private fun handleFileSearch(peer: DeviceInfo, packet: Packet) {
        val (results, truncated) = fileSearch.search(packet.getString("query") ?: "")
        node.sendTo(peer.deviceId, Packet.create(PacketType.FILE_SEARCH_RESULT) {
            put("requestId", packet.getString("requestId") ?: "")
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
        ConduitRuntime.setSearchResults(list, packet.getBool("truncated"))
    }

    /** Push current battery + device status to all peers (called on connect). */
    fun pushStatus() {
        battery.sendNow()
        status.sendNow()
    }
}
