package io.conduit.features

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioManager
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType

/**
 * Reports Wi-Fi SSID, signal, and ringer mode to the PC — once on connect and again
 * whenever the ringer mode or network changes, so the PC's Phone Status stays live.
 */
class DeviceStatusFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("DeviceStatus")

    // Fires when the user flips silent/vibrate/normal or the Wi-Fi connection changes.
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context?, intent: Intent?) = sendNow()
    }

    fun start() {
        val filter = IntentFilter().apply {
            addAction(AudioManager.RINGER_MODE_CHANGED_ACTION)
            addAction(WifiManager.NETWORK_STATE_CHANGED_ACTION)
            @Suppress("DEPRECATION")
            addAction(ConnectivityManager.CONNECTIVITY_ACTION)
        }
        context.registerReceiver(receiver, filter)
    }

    fun stop() {
        runCatching { context.unregisterReceiver(receiver) }
    }

    @Suppress("DEPRECATION")
    fun sendNow() {
        try {
            val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val info = wifi.connectionInfo
            // SSID is wrapped in quotes and requires location permission on Android 9+.
            val ssid = info?.ssid?.trim('"')?.takeIf { it.isNotEmpty() && it != "<unknown ssid>" } ?: ""
            val signal = info?.rssi ?: 0

            val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
            val ringer = when (audio.ringerMode) {
                AudioManager.RINGER_MODE_SILENT -> "silent"
                AudioManager.RINGER_MODE_VIBRATE -> "vibrate"
                else -> "normal"
            }

            node.broadcast(Packet.create(PacketType.DEVICE_STATUS) {
                put("ssid", ssid); put("signal", signal); put("ringerMode", ringer)
            })
            log.d("Status ssid=$ssid ringer=$ringer")
        } catch (e: Exception) {
            log.w(e, "Failed to read device status")
        }
    }
}
