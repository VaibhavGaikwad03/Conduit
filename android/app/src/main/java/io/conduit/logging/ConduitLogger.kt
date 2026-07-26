package io.conduit.logging

import android.content.Context
import timber.log.Timber
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

/**
 * Logging entry point. Plants a debug tree (visible via `adb logcat -s Conduit`) plus a
 * rotating file tree so runtime bugs can be inspected on-device without a cable.
 *
 * Files land in:  <app files>/logs/conduit-<date>.log   (surfaced in the app's Logs screen).
 */
object ConduitLog {
    fun init(context: Context) {
        Timber.plant(Timber.DebugTree())
        Timber.plant(FileLoggingTree(context.applicationContext))
        tag("Startup").i("Conduit logging initialized")
    }

    /** Convenience for a tagged logger, e.g. ConduitLog.tag("Discovery").i("..."). */
    fun tag(tag: String): TaggedLogger = TaggedLogger("Conduit/$tag")
}

/**
 * A cached, reusable logger bound to one subsystem tag. Unlike caching Timber.tag()'s
 * return value (whose thread-local tag is consumed after a single call), this re-applies
 * the tag on every call so log lines always carry the correct subsystem tag.
 */
class TaggedLogger(private val tag: String) {
    fun v(message: String) = Timber.tag(tag).v(message)
    fun v(t: Throwable?, message: String) = Timber.tag(tag).v(t, message)
    fun d(message: String) = Timber.tag(tag).d(message)
    fun d(t: Throwable?, message: String) = Timber.tag(tag).d(t, message)
    fun i(message: String) = Timber.tag(tag).i(message)
    fun w(message: String) = Timber.tag(tag).w(message)
    fun w(t: Throwable?, message: String) = Timber.tag(tag).w(t, message)
    fun e(message: String) = Timber.tag(tag).e(message)
    fun e(t: Throwable?, message: String) = Timber.tag(tag).e(t, message)
}

/**
 * Writes every log to a daily file, rotating and pruning to the most recent 14 days.
 * All disk I/O happens on a single background thread to stay off the main thread.
 */
class FileLoggingTree(context: Context) : Timber.Tree() {
    private val logDir = File(context.filesDir, "logs").apply { mkdirs() }
    private val io = Executors.newSingleThreadExecutor()
    private val dateFmt = SimpleDateFormat("yyyy-MM-dd", Locale.US)
    private val timeFmt = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US)

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val line = buildString {
            append(timeFmt.format(Date()))
            append(' ').append(levelChar(priority))
            append(" [").append(tag ?: "Conduit").append("] ")
            append(message)
            if (t != null) append('\n').append(android.util.Log.getStackTraceString(t))
        }
        io.execute {
            try {
                currentFile().appendText(line + "\n")
            } catch (_: Exception) {
                // Never let logging crash the app.
            }
        }
    }

    fun logDirectory(): File = logDir

    private fun currentFile(): File {
        pruneOld()
        return File(logDir, "conduit-${dateFmt.format(Date())}.log")
    }

    private fun pruneOld() {
        val files = logDir.listFiles { f -> f.name.startsWith("conduit-") } ?: return
        if (files.size <= 14) return
        files.sortedBy { it.lastModified() }
            .take(files.size - 14)
            .forEach { it.delete() }
    }

    private fun levelChar(priority: Int) = when (priority) {
        android.util.Log.VERBOSE -> "V"
        android.util.Log.DEBUG -> "D"
        android.util.Log.INFO -> "I"
        android.util.Log.WARN -> "W"
        android.util.Log.ERROR -> "E"
        else -> "?"
    }
}
