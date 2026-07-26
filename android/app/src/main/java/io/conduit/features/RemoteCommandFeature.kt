package io.conduit.features

import android.content.Context
import android.media.AudioManager
import android.media.RingtoneManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import io.conduit.logging.ConduitLog

/** Executes remote commands from the PC: ring the phone, etc. */
class RemoteCommandFeature(private val context: Context) {
    private val log = ConduitLog.tag("Remote")

    fun handle(command: String) {
        log.i("Remote command: $command")
        when (command) {
            "ring" -> ring()
            "lock" -> log.w("Remote lock requires device-admin; not enabled in this build")
            else -> log.w("Unhandled remote command $command")
        }
    }

    private fun ring() {
        try {
            val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
            audio.setStreamVolume(
                AudioManager.STREAM_RING,
                audio.getStreamMaxVolume(AudioManager.STREAM_RING), 0,
            )
            val uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            RingtoneManager.getRingtone(context, uri)?.play()

            val vibrator = context.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                vibrator.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 500, 300, 500), 0))
            } else {
                @Suppress("DEPRECATION")
                vibrator.vibrate(longArrayOf(0, 500, 300, 500), 0)
            }
            log.i("Ringing phone")
        } catch (e: Exception) {
            log.e(e, "Failed to ring")
        }
    }
}
