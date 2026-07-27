package io.conduit.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import io.conduit.features.FeatureHub
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.runtime.ConduitRuntime
import io.conduit.storage.AppStore
import io.conduit.ui.MainActivity

/**
 * Long-running foreground service that owns the ConduitNode, keeps discovery + the encrypted
 * session alive, and routes packets into the feature hub.
 */
class ConduitService : Service() {
    private val log = ConduitLog.tag("Service")
    private lateinit var node: ConduitNode
    private lateinit var hub: FeatureHub
    private var multicastLock: WifiManager.MulticastLock? = null

    override fun onCreate() {
        super.onCreate()
        acquireMulticastLock()

        val store = AppStore(this)
        node = ConduitNode(store)
        hub = FeatureHub(applicationContext, node)

        node.onDevicesChanged = { ConduitRuntime.refreshDevices() }
        node.onPeerConnected = { peer ->
            ConduitRuntime.lastEvent.value = "Connected: ${peer.name}"
            hub.pushStatus()
            ConduitRuntime.refreshDevices()
        }
        node.onPeerDisconnected = { peer ->
            ConduitRuntime.lastEvent.value = "Disconnected: ${peer.name}"
            ConduitRuntime.refreshDevices()
        }

        ConduitRuntime.node = node
        ConduitRuntime.files = hub.files
        node.start()
        hub.start()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIF_ID, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIF_ID, buildNotification())
        }
        log.i("Conduit service started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        hub.stop()
        node.stop()
        ConduitRuntime.node = null
        ConduitRuntime.files = null
        multicastLock?.let { if (it.isHeld) it.release() }
        log.i("Conduit service stopped")
        super.onDestroy()
    }

    private fun acquireMulticastLock() {
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            multicastLock = wifi.createMulticastLock("conduit").apply {
                setReferenceCounted(true)
                acquire()
            }
            log.d("Multicast lock acquired")
        } catch (e: Exception) {
            log.w(e, "Could not acquire multicast lock")
        }
    }

    private fun buildNotification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Conduit", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "Keeps your phone and PC connected"
                },
            )
        }
        val pending = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Conduit")
            .setContentText("Connected to your ecosystem")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(pending)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "conduit_service"
        private const val NOTIF_ID = 1001

        fun start(context: Context) {
            val intent = Intent(context, ConduitService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }
    }
}
