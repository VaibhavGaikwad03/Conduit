package io.conduit.features

import android.content.ComponentName
import android.content.Context
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType

/**
 * Reports what's playing on the phone (title / artist / playing) to the PC and keeps it live.
 * Reads the active media sessions via [MediaSessionManager], which is allowed because Conduit's
 * notification listener ([ConduitNotificationListener]) is enabled. Re-sends whenever the set of
 * sessions, the playback state, or the track metadata changes.
 */
class MediaStateFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("MediaState")
    private val handler = Handler(Looper.getMainLooper())
    private val component = ComponentName(context, ConduitNotificationListener::class.java)
    private val manager =
        context.getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager

    private var controllers = emptyList<MediaController>()
    private val callbacks = HashMap<MediaController, MediaController.Callback>()

    private val sessionsListener =
        MediaSessionManager.OnActiveSessionsChangedListener { rebind(it ?: emptyList()) }

    fun start() {
        try {
            manager.addOnActiveSessionsChangedListener(sessionsListener, component, handler)
            rebind(manager.getActiveSessions(component))
        } catch (e: SecurityException) {
            // Notification access not granted yet — nothing to read until the user enables it.
            log.w(e, "No notification access; can't read media sessions")
        } catch (e: Exception) {
            log.w(e, "Failed to start media state reporting")
        }
    }

    fun stop() {
        runCatching { manager.removeOnActiveSessionsChangedListener(sessionsListener) }
        controllers.forEach { c -> callbacks.remove(c)?.let { c.unregisterCallback(it) } }
        controllers = emptyList()
    }

    /** Read and send the current now-playing state immediately (used on connect). */
    fun sendNow() = broadcast()

    // Attach callbacks to the current set of sessions and drop the old ones, then push an update.
    private fun rebind(list: List<MediaController>) {
        controllers.forEach { c -> callbacks.remove(c)?.let { c.unregisterCallback(it) } }
        controllers = list
        list.forEach { c ->
            val cb = object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) = broadcast()
                override fun onMetadataChanged(metadata: MediaMetadata?) = broadcast()
                override fun onSessionDestroyed() = broadcast()
            }
            callbacks[c] = cb
            c.registerCallback(cb, handler)
        }
        broadcast()
    }

    // Prefer a session that is actually playing; otherwise the most recent one, if any.
    private fun activeController(): MediaController? =
        controllers.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
            ?: controllers.firstOrNull()

    private fun broadcast() {
        try {
            val c = activeController()
            val md = c?.metadata
            val ps = c?.playbackState
            val title = md?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: ""
            val artist = md?.getString(MediaMetadata.METADATA_KEY_ARTIST)
                ?: md?.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST) ?: ""
            val playing = ps?.state == PlaybackState.STATE_PLAYING
            val position = ps?.position ?: 0L
            val duration = md?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L

            node.broadcast(Packet.create(PacketType.MEDIA_STATE) {
                put("title", title)
                put("artist", artist)
                put("app", c?.packageName ?: "")
                put("playing", playing)
                put("position", position)
                put("duration", duration)
            })
            log.d("Now playing '$title' by '$artist' playing=$playing")
        } catch (e: Exception) {
            log.w(e, "Failed to read media state")
        }
    }
}
