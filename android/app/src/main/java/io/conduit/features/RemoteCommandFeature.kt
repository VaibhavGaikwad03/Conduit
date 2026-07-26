package io.conduit.features

import android.content.Context
import io.conduit.logging.ConduitLog

/** Executes remote commands from the PC: ring / stop-ring the phone, etc. */
class RemoteCommandFeature(private val context: Context) {
    private val log = ConduitLog.tag("Remote")

    fun handle(command: String) {
        log.i("Remote command: $command")
        when (command) {
            "ring" -> Ringer.start(context)
            "ring-stop" -> Ringer.stop(context)
            "lock" -> log.w("Remote lock requires device-admin; not enabled in this build")
            else -> log.w("Unhandled remote command $command")
        }
    }
}
