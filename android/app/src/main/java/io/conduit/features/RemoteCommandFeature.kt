package io.conduit.features

import android.app.admin.DevicePolicyManager
import android.content.Context
import io.conduit.logging.ConduitLog
import io.conduit.runtime.ConduitRuntime

/** Executes remote commands from the PC: ring / stop-ring, lock, etc. */
class RemoteCommandFeature(private val context: Context) {
    private val log = ConduitLog.tag("Remote")

    fun handle(command: String) {
        log.i("Remote command: $command")
        when (command) {
            "ring" -> Ringer.start(context)
            "ring-stop" -> Ringer.stop(context)
            "lock" -> lock()
            else -> log.w("Unhandled remote command $command")
        }
    }

    private fun lock() {
        val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
        val admin = ConduitDeviceAdminReceiver.component(context)
        if (dpm.isAdminActive(admin)) {
            try {
                dpm.lockNow()
                log.i("Phone locked")
            } catch (e: Exception) {
                log.e(e, "lockNow() failed")
            }
        } else {
            // Can't lock until the user grants device-admin. Nudge them in the app.
            log.w("Lock requested but device-admin is not enabled")
            ConduitRuntime.lastEvent.value =
                "Enable 'Remote lock' in Conduit to let your PC lock this phone"
        }
    }
}
