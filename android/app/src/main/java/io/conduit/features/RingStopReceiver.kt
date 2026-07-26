package io.conduit.features

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Stops the ring when the user taps "Stop" on the ring notification. */
class RingStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action == Ringer.ACTION_STOP) {
            Ringer.stop(context)
        }
    }
}
