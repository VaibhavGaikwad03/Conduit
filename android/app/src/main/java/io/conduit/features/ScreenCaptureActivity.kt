package io.conduit.features

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import io.conduit.logging.ConduitLog
import io.conduit.service.ConduitService

/**
 * A tiny, invisible activity whose only job is to show the system "Start screen capture?" dialog.
 * MediaProjection consent can only be requested from an Activity, but the PC's screen-start request
 * arrives in the background service — and Android blocks starting an activity from the background.
 * So we surface it via a full-screen-intent notification (see [promptForCapture]); tapping it (or
 * the system auto-launching the full-screen intent) brings us up, and we hand the result back.
 */
class ScreenCaptureActivity : Activity() {
    private val log = ConduitLog.tag("Screen")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Clear the prompt notification now that we're up.
        getSystemService(NotificationManager::class.java)?.cancel(NOTIF_ID)

        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        try {
            startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST_CODE)
        } catch (e: Exception) {
            log.e(e, "Could not launch screen-capture consent")
            ConduitService.instance?.onScreenCaptureResult(RESULT_CANCELED, null)
            finish()
        }
    }

    @Deprecated("startActivityForResult result callback")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQUEST_CODE) {
            ConduitService.instance?.onScreenCaptureResult(resultCode, data)
        }
        finish()
    }

    companion object {
        private const val REQUEST_CODE = 7461
        private const val NOTIF_ID = 1002
        private const val CHANNEL_ID = "conduit_screen_request"

        /**
         * Ask the user for screen-capture consent. Posts a high-priority full-screen-intent
         * notification (reliable from the background, unlike a bare activity start) and also tries
         * a direct launch, which succeeds instantly when the app happens to be in the foreground.
         */
        fun promptForCapture(context: Context) {
            val nm = context.getSystemService(NotificationManager::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, "Screen mirror requests", NotificationManager.IMPORTANCE_HIGH).apply {
                        description = "Prompts to allow mirroring your screen to a paired PC"
                    },
                )
            }
            val intent = Intent(context, ScreenCaptureActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            val pending = PendingIntent.getActivity(
                context, 0, intent,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val notification = Notification.Builder(context, CHANNEL_ID)
                .setContentTitle("Mirror your screen to the PC?")
                .setContentText("Your paired PC is asking to show your screen. Tap to allow.")
                .setSmallIcon(io.conduit.R.drawable.ic_stat_conduit)
                .setContentIntent(pending)
                .setFullScreenIntent(pending, true)
                .setAutoCancel(true)
                .build()
            nm.notify(NOTIF_ID, notification)

            // Fast path: if we're already foregrounded, this shows the dialog immediately.
            try { context.startActivity(intent) } catch (_: Exception) { /* background start blocked; notification covers it */ }
        }
    }
}
