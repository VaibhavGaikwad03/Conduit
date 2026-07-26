package io.conduit.features

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType

/** Reports battery level, charging state and temperature to the PC whenever they change. */
class BatteryFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("Battery")

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context?, intent: Intent?) = broadcast(intent)
    }

    fun start() {
        context.registerReceiver(receiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
    }

    fun stop() {
        runCatching { context.unregisterReceiver(receiver) }
    }

    fun sendNow() {
        val intent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        broadcast(intent)
    }

    private fun broadcast(intent: Intent?) {
        intent ?: return
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        val pct = if (level >= 0 && scale > 0) level * 100 / scale else -1
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
            status == BatteryManager.BATTERY_STATUS_FULL
        val temp = intent.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) / 10

        node.broadcast(Packet.create(PacketType.BATTERY) {
            put("level", pct); put("charging", charging); put("temperature", temp)
        })
        log.d("Battery $pct% charging=$charging")
    }
}
