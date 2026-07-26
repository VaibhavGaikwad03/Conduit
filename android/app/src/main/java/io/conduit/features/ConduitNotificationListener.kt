package io.conduit.features

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import io.conduit.logging.ConduitLog
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime

/**
 * Mirrors posted notifications to the PC and lets the PC dismiss them.
 * Enable it under Settings → Notifications → Notification access → Conduit.
 */
class ConduitNotificationListener : NotificationListenerService() {
    private val log = ConduitLog.tag("Notifications")

    override fun onListenerConnected() {
        instance = this
        log.i("Notification listener connected")
    }

    override fun onListenerDisconnected() {
        instance = null
        log.i("Notification listener disconnected")
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        val node = ConduitRuntime.node ?: return
        val extras = sbn.notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() ?: ""
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString() ?: ""
        if (title.isEmpty() && text.isEmpty()) return
        // Skip our own foreground-service notification.
        if (sbn.packageName == packageName) return

        val appName = appLabel(sbn.packageName)
        node.broadcast(Packet.create(PacketType.NOTIFICATION) {
            put("key", sbn.key)
            put("appName", appName)
            put("title", title)
            put("text", text)
            put("canReply", false)
        })
        log.d("Mirrored notification from $appName: $title")
    }

    fun handleAction(packet: Packet) {
        val key = packet.getString("key") ?: return
        when (packet.getString("action")) {
            "dismiss" -> {
                cancelNotification(key)
                log.i("Dismissed notification $key from PC")
            }
            else -> log.d("Unhandled notification action")
        }
    }

    private fun appLabel(pkg: String): String = try {
        val pm = packageManager
        pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()
    } catch (_: Exception) {
        pkg
    }

    companion object {
        @Volatile var instance: ConduitNotificationListener? = null
    }
}
