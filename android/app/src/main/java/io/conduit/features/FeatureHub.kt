package io.conduit.features

import android.content.Context
import io.conduit.logging.ConduitLog
import io.conduit.model.DeviceInfo
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType

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
                else -> log.d("Unhandled packet type ${packet.type}")
            }
        } catch (e: Exception) {
            log.e(e, "Error handling ${packet.type} from ${peer.name}")
        }
    }

    /** Push current battery + device status to all peers (called on connect). */
    fun pushStatus() {
        battery.sendNow()
        status.sendNow()
    }
}
