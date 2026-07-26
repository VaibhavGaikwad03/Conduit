package io.conduit.features

import android.app.admin.DeviceAdminReceiver
import android.content.ComponentName
import android.content.Context
import io.conduit.logging.ConduitLog

/**
 * Registers Conduit as a Device Admin so the PC can lock the phone via
 * DevicePolicyManager.lockNow() (needs the force-lock policy in res/xml/device_admin.xml).
 * The user must grant this once from the app.
 */
class ConduitDeviceAdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: android.content.Intent) {
        ConduitLog.tag("Remote").i("Device admin enabled — remote lock available")
    }

    override fun onDisabled(context: Context, intent: android.content.Intent) {
        ConduitLog.tag("Remote").i("Device admin disabled — remote lock unavailable")
    }

    companion object {
        fun component(context: Context) =
            ComponentName(context.applicationContext, ConduitDeviceAdminReceiver::class.java)
    }
}
