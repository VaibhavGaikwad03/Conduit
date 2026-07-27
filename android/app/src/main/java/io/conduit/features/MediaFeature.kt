package io.conduit.features

import android.content.Context
import android.media.AudioManager
import android.os.SystemClock
import android.view.KeyEvent
import io.conduit.logging.ConduitLog

/**
 * Applies media commands from the PC to whatever is playing on the phone, by dispatching
 * media key events and adjusting volume through AudioManager.
 */
class MediaFeature(context: Context) {
    private val log = ConduitLog.tag("Media")
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    fun handle(command: String, value: Double?) {
        log.i("Media command: $command ${value ?: ""}")
        when (command) {
            "play", "pause" -> tap(KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE)
            "next" -> tap(KeyEvent.KEYCODE_MEDIA_NEXT)
            "prev" -> tap(KeyEvent.KEYCODE_MEDIA_PREVIOUS)
            "volume" -> setVolume(value ?: 0.5)
            "mute" -> audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_MUTE, 0)
            else -> log.w("Unknown media command $command")
        }
    }

    private fun setVolume(fraction: Double) {
        val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        val target = (fraction.coerceIn(0.0, 1.0) * max).toInt()
        audio.setStreamVolume(AudioManager.STREAM_MUSIC, target, 0)
    }

    private fun tap(keyCode: Int) {
        // Both events must carry a real, shared downTime; media sessions pair the UP to a
        // matching DOWN by timestamp and silently drop key events stamped 0 (the default from
        // the two-arg KeyEvent constructor), which is what made playback control flaky.
        val now = SystemClock.uptimeMillis()
        audio.dispatchMediaKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_DOWN, keyCode, 0))
        audio.dispatchMediaKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_UP, keyCode, 0))
    }
}
