package io.conduit.features

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioManager
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType

/**
 * Reports the ringer mode (silent / vibrate / normal) to the PC — once on connect and again
 * whenever it changes, so the PC's Phone Status stays live.
 *
 * (Wi-Fi SSID is intentionally not reported: on Android 12+ the network name is location-
 * sensitive and gets redacted for a background service, so it was always blank — the PC's
 * Phone Status no longer shows a Wi-Fi row.)
 */
class DeviceStatusFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("DeviceStatus")

    private val ringerReceiver = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context?, intent: Intent?) = sendNow()
    }

    fun start() {
        context.registerReceiver(ringerReceiver, IntentFilter(AudioManager.RINGER_MODE_CHANGED_ACTION))
    }

    fun stop() {
        runCatching { context.unregisterReceiver(ringerReceiver) }
    }

    fun sendNow() {
        try {
            val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
            val ringer = when (audio.ringerMode) {
                AudioManager.RINGER_MODE_SILENT -> "silent"
                AudioManager.RINGER_MODE_VIBRATE -> "vibrate"
                else -> "normal"
            }
            node.broadcast(Packet.create(PacketType.DEVICE_STATUS) {
                put("ringerMode", ringer)
            })
            log.d("Ringer $ringer")
        } catch (e: Exception) {
            log.w(e, "Failed to read device status")
        }
    }
}
