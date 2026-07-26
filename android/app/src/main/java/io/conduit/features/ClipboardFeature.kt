package io.conduit.features

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import io.conduit.logging.ConduitLog

/**
 * Two-way clipboard sync. Applies remote text to the local clipboard and (while the app is
 * foregrounded, per Android 10+ policy) reports local changes so they can be pushed to the PC.
 */
class ClipboardFeature(context: Context) {
    private val log = ConduitLog.tag("Clipboard")
    private val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    private val main = Handler(Looper.getMainLooper())
    private var lastValue: String? = null

    var onLocalChange: ((String) -> Unit)? = null

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        val text = cm.primaryClip?.getItemAt(0)?.coerceToText(null)?.toString().orEmpty()
        if (text.isNotEmpty() && text != lastValue) {
            lastValue = text
            log.i("Local clipboard changed (${text.length} chars)")
            onLocalChange?.invoke(text)
        }
    }

    fun start() = cm.addPrimaryClipChangedListener(listener)
    fun stop() = cm.removePrimaryClipChangedListener(listener)

    fun setFromRemote(text: String) {
        lastValue = text // suppress echo
        main.post {
            cm.setPrimaryClip(ClipData.newPlainText("Conduit", text))
            log.i("Clipboard updated from remote (${text.length} chars)")
        }
    }
}
