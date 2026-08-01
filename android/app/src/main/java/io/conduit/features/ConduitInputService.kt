package io.conduit.features

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Path
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.DisplayMetrics
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import io.conduit.logging.ConduitLog

/**
 * Accessibility service that lets the paired PC control this phone while its screen is mirrored:
 * it injects taps/swipes via gesture dispatch, presses the back/home/recents buttons, and types
 * into the focused field. This is the only no-root way to synthesize input into other apps. The
 * user enables it once in Settings → Accessibility; [promptEnable] surfaces that when needed.
 */
class ConduitInputService : AccessibilityService() {
    private val log = ConduitLog.tag("Input")

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        log.i("Remote input service connected")
    }

    override fun onUnbind(intent: Intent?): Boolean {
        if (instance === this) instance = null
        log.i("Remote input service disconnected")
        return super.onUnbind(intent)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) { /* input-only; we don't observe */ }
    override fun onInterrupt() {}

    /** Single tap at a normalized (0..1) screen coordinate. */
    fun tap(nx: Float, ny: Float) {
        val (w, h) = screenPx()
        val path = Path().apply { moveTo((nx * w), (ny * h)) }
        dispatch(GestureDescription.StrokeDescription(path, 0, 50))
    }

    /** Swipe/drag from one normalized point to another over durationMs. */
    fun swipe(nx1: Float, ny1: Float, nx2: Float, ny2: Float, durationMs: Long) {
        val (w, h) = screenPx()
        val path = Path().apply {
            moveTo(nx1 * w, ny1 * h)
            lineTo(nx2 * w, ny2 * h)
        }
        dispatch(GestureDescription.StrokeDescription(path, 0, durationMs.coerceIn(20, 3000)))
    }

    /** Hardware-style keys and editor actions. */
    fun key(name: String?) {
        when (name) {
            "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
            "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
            "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
            "enter" -> imeEnterOrNewline()
            "backspace" -> backspace()
            else -> log.d("Unknown key $name")
        }
    }

    /** Append typed text into the currently focused editable field (best effort). */
    fun typeText(text: String) {
        val node = focusedEditable() ?: run { log.d("No focused field to type into"); return }
        val current = node.text?.toString() ?: ""
        setNodeText(node, current + text)
    }

    private fun backspace() {
        val node = focusedEditable() ?: return
        val current = node.text?.toString() ?: ""
        if (current.isNotEmpty()) setNodeText(node, current.dropLast(1))
    }

    private fun imeEnterOrNewline() {
        val node = focusedEditable() ?: return
        val done = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        } else {
            false
        }
        if (!done) {
            val current = node.text?.toString() ?: ""
            setNodeText(node, current + "\n")
        }
    }

    private fun setNodeText(node: AccessibilityNodeInfo, value: String) {
        val args = Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
        // Keep the caret at the end so the next keystroke appends.
        val sel = Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, value.length)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, value.length)
        }
        node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, sel)
    }

    private fun focusedEditable(): AccessibilityNodeInfo? =
        findFocus(AccessibilityNodeInfo.FOCUS_INPUT)?.takeIf { it.isEditable }

    private fun dispatch(stroke: GestureDescription.StrokeDescription) {
        try {
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        } catch (e: Exception) {
            log.w(e, "Gesture dispatch failed")
        }
    }

    private fun screenPx(): Pair<Int, Int> {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
            val b = wm.maximumWindowMetrics.bounds
            b.width() to b.height()
        } else {
            val m = DisplayMetrics()
            @Suppress("DEPRECATION")
            (getSystemService(Context.WINDOW_SERVICE) as WindowManager).defaultDisplay.getRealMetrics(m)
            m.widthPixels to m.heightPixels
        }
    }

    companion object {
        @Volatile
        var instance: ConduitInputService? = null
            private set

        private const val NOTIF_ID = 1003
        private const val CHANNEL_ID = "conduit_remote_input"

        /** True if our accessibility service is currently enabled by the user. */
        fun isEnabled(context: Context): Boolean {
            val flat = Settings.Secure.getString(
                context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
            ) ?: return false
            return flat.split(':').any { it.contains(context.packageName + "/") && it.contains("ConduitInputService") }
        }

        /**
         * The PC asked to control the phone but the service isn't enabled — post a notification that
         * opens Accessibility settings so the user can turn on "Conduit Remote Control".
         */
        fun promptEnable(context: Context) {
            val nm = context.getSystemService(NotificationManager::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, "Remote control", NotificationManager.IMPORTANCE_HIGH).apply {
                        description = "Prompts to enable controlling this phone from a paired PC"
                    },
                )
            }
            val intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            val pending = PendingIntent.getActivity(
                context, 0, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val notification = Notification.Builder(context, CHANNEL_ID)
                .setContentTitle("Enable control from your PC")
                .setContentText("Turn on 'Conduit Remote Control' in Accessibility to let the PC tap and type.")
                .setSmallIcon(android.R.drawable.ic_menu_edit)
                .setContentIntent(pending)
                .setAutoCancel(true)
                .build()
            nm.notify(NOTIF_ID, notification)
        }
    }
}
