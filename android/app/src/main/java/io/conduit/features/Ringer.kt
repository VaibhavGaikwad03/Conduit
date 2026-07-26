package io.conduit.features

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import io.conduit.R
import io.conduit.logging.ConduitLog

/**
 * Owns the "find my phone" ring so it can actually be stopped. Plays the ringtone on the
 * alarm stream (so it sounds even in silent mode), vibrates, and posts a high-priority
 * notification with a Stop button. Also auto-stops after a timeout so it never rings forever.
 *
 * A singleton so the Stop notification's broadcast can end the ring regardless of which
 * component started it (PC command, etc.).
 */
object Ringer {
    private val log = ConduitLog.tag("Ring")
    private const val CHANNEL_ID = "conduit_ring"
    private const val NOTIF_ID = 2002
    private const val AUTO_STOP_MS = 60_000L
    const val ACTION_STOP = "io.conduit.action.STOP_RING"

    private val main = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var vibrator: Vibrator? = null
    private var previousAlarmVolume: Int? = null

    val isRinging: Boolean get() = player != null

    @Synchronized
    fun start(context: Context) {
        val app = context.applicationContext
        if (isRinging) { log.d("Already ringing"); return }
        try {
            // Raise the alarm volume so it's audible (guarded: Do-Not-Disturb can block this).
            try {
                val audio = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
                previousAlarmVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
                audio.setStreamVolume(
                    AudioManager.STREAM_ALARM,
                    audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0,
                )
            } catch (e: Exception) {
                log.w(e, "Could not raise alarm volume")
            }

            // Play our bundled alarm tone from res/raw — reliable on every device, unlike
            // OEM default-ringtone URIs which MediaPlayer can't always open.
            player = MediaPlayer().apply {
                app.resources.openRawResourceFd(R.raw.conduit_ring).use { afd ->
                    setDataSource(afd.fileDescriptor, afd.startOffset, afd.length)
                }
                setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ALARM)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build(),
                )
                isLooping = true
                prepare()
                start()
            }

            vibrator = (app.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator).also { v ->
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    v.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 600, 400, 600), 0))
                } else {
                    @Suppress("DEPRECATION")
                    v.vibrate(longArrayOf(0, 600, 400, 600), 0)
                }
            }

            postStopNotification(app)
            main.postDelayed({ stop(app) }, AUTO_STOP_MS)
            log.i("Ringing phone (stop via notification, PC command, or ${AUTO_STOP_MS / 1000}s timeout)")
        } catch (e: Exception) {
            log.e(e, "Failed to start ring")
            stop(app)
        }
    }

    @Synchronized
    fun stop(context: Context) {
        val app = context.applicationContext
        main.removeCallbacksAndMessages(null)
        try { player?.stop() } catch (_: Exception) {}
        player?.release()
        player = null
        vibrator?.cancel()
        vibrator = null

        // Restore the user's previous alarm volume.
        previousAlarmVolume?.let {
            (app.getSystemService(Context.AUDIO_SERVICE) as AudioManager)
                .setStreamVolume(AudioManager.STREAM_ALARM, it, 0)
        }
        previousAlarmVolume = null

        (app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(NOTIF_ID)
        log.i("Ring stopped")
    }

    private fun postStopNotification(app: Context) {
        val nm = app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Ring phone", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "Shown when your PC rings this phone"
                    setSound(null, null) // we play the sound ourselves via MediaPlayer
                },
            )
        }
        val stopPending = PendingIntent.getBroadcast(
            app, 0, Intent(app, RingStopReceiver::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        val notification = Notification.Builder(app, CHANNEL_ID)
            .setContentTitle("Conduit is ringing this phone")
            .setContentText("Tap Stop to silence it.")
            .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
            .setCategory(Notification.CATEGORY_ALARM)
            .setOngoing(true)
            .setAutoCancel(false)
            .addAction(
                Notification.Action.Builder(null, "Stop", stopPending).build(),
            )
            .setContentIntent(stopPending)
            .build()

        nm.notify(NOTIF_ID, notification)
    }
}
