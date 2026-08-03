package io.conduit.features

import android.app.DownloadManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Base64
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime
import io.conduit.runtime.TransferUi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Sends and receives files as base64 chunks (file-offer → file-chunk* → file-complete).
 * Incoming files are saved to the phone's public **Downloads** folder so they show up in
 * the Downloads app and any file manager.
 */
class FileFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("FileTransfer")
    private val scope = CoroutineScope(Dispatchers.IO)
    private val incoming = ConcurrentHashMap<String, Incoming>()

    /** One in-progress incoming transfer. Writes to Downloads via a MediaStore URI (Q+) or a file. */
    private class Incoming(
        val transferId: String,
        val displayName: String,
        val total: Long,
        val out: OutputStream,
        val uri: Uri?,          // non-null on Android 10+ (MediaStore)
        val legacyFile: File?,  // non-null on Android 9 and below
    ) {
        var nextSeq = 0
        var received = 0L
        var lastPercent = -1
        val pending = sortedMapOf<Int, ByteArray>()
    }

    private fun autoRemove(id: String) {
        scope.launch { delay(4000); ConduitRuntime.removeTransfer(id) }
    }

    // ---- Sending --------------------------------------------------------------

    fun sendFile(deviceId: String, uri: Uri) {
        scope.launch {
            try {
                val name = queryName(uri)
                val size = querySize(uri)
                val transferId = UUID.randomUUID().toString().replace("-", "")
                log.i("Sending $name ($size bytes) to $deviceId")
                ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, 0, size))

                node.sendTo(deviceId, Packet.create(PacketType.FILE_OFFER) {
                    put("transferId", transferId); put("name", name)
                    put("size", size); put("mime", "application/octet-stream")
                })

                val sha = MessageDigest.getInstance("SHA-256")
                var sent = 0L
                var lastPercent = -1
                context.contentResolver.openInputStream(uri)?.use { input ->
                    val buffer = ByteArray(64 * 1024)
                    var seq = 0
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        sha.update(buffer, 0, read)
                        val b64 = Base64.encodeToString(buffer.copyOf(read), Base64.NO_WRAP)
                        val thisSeq = seq++
                        node.sendTo(deviceId, Packet.create(PacketType.FILE_CHUNK) {
                            put("transferId", transferId); put("seq", thisSeq); put("dataB64", b64)
                        })
                        sent += read
                        val pct = if (size > 0) ((sent * 100) / size).toInt() else 0
                        if (pct != lastPercent) {
                            lastPercent = pct
                            ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, sent, size))
                        }
                    }
                }
                val hash = sha.digest().joinToString("") { "%02x".format(it) }
                node.sendTo(deviceId, Packet.create(PacketType.FILE_COMPLETE) {
                    put("transferId", transferId); put("ok", true); put("sha256", hash)
                })
                ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, size, size, done = true))
                autoRemove(transferId)
                log.i("Finished sending $name")
            } catch (e: Exception) {
                log.e(e, "Failed to send file")
            }
        }
    }

    // ---- Receiving ------------------------------------------------------------

    fun handle(packet: Packet) {
        when (packet.type) {
            PacketType.FILE_OFFER -> begin(packet)
            PacketType.FILE_CHUNK -> chunk(packet)
            PacketType.FILE_COMPLETE -> complete(packet)
        }
    }

    private fun begin(packet: Packet) {
        val transferId = packet.getString("transferId") ?: return
        val name = File(packet.getString("name") ?: "conduit-file").name
        val size = packet.getLong("size")
        val mime = packet.getString("mime")?.takeIf { it.isNotBlank() } ?: "application/octet-stream"
        try {
            val inc = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                // Scoped storage: write into public Downloads via MediaStore (no permission needed).
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, name)
                    put(MediaStore.Downloads.MIME_TYPE, mime)
                    put(MediaStore.Downloads.IS_PENDING, 1)
                }
                val resolver = context.contentResolver
                val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                    ?: throw IOException("MediaStore insert returned null")
                val out = resolver.openOutputStream(uri) ?: throw IOException("openOutputStream returned null")
                Incoming(transferId, name, size, out, uri, null)
            } else {
                @Suppress("DEPRECATION")
                val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
                dir.mkdirs()
                val dest = uniqueFile(File(dir, name))
                Incoming(transferId, name, size, FileOutputStream(dest), null, dest)
            }
            incoming[transferId] = inc
            log.i("Receiving $name → Downloads")
            ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = false, 0, size))
        } catch (e: Exception) {
            log.e(e, "Failed to start receiving $name")
        }
    }

    private fun chunk(packet: Packet) {
        val transferId = packet.getString("transferId") ?: return
        val inc = incoming[transferId] ?: return
        val seq = packet.getInt("seq")
        val data = Base64.decode(packet.getString("dataB64") ?: "", Base64.NO_WRAP)
        inc.pending[seq] = data
        while (inc.pending.containsKey(inc.nextSeq)) {
            val bytes = inc.pending.remove(inc.nextSeq)!!
            inc.out.write(bytes)
            inc.received += bytes.size
            inc.nextSeq++
        }
        val pct = if (inc.total > 0) ((inc.received * 100) / inc.total).toInt() else 0
        if (pct != inc.lastPercent) {
            inc.lastPercent = pct
            ConduitRuntime.upsertTransfer(
                TransferUi(inc.transferId, inc.displayName, sending = false, inc.received, inc.total),
            )
        }
    }

    private fun complete(packet: Packet) {
        val transferId = packet.getString("transferId") ?: return
        val inc = incoming.remove(transferId) ?: return
        try {
            inc.out.flush()
            inc.out.close()

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && inc.uri != null) {
                // Publish the file (clear IS_PENDING so it's visible in Downloads).
                val values = ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }
                context.contentResolver.update(inc.uri, values, null, null)
            } else if (inc.legacyFile != null) {
                MediaScannerConnection.scanFile(context, arrayOf(inc.legacyFile.absolutePath), null, null)
            }

            log.i("Received file → Downloads/${inc.displayName}")
            ConduitRuntime.upsertTransfer(
                TransferUi(inc.transferId, inc.displayName, sending = false, inc.total, inc.total, done = true),
            )
            ConduitRuntime.lastEvent.value = "Saved ${inc.displayName} to Downloads"
            autoRemove(inc.transferId)
            notifyReceived(inc.displayName)
        } catch (e: Exception) {
            log.e(e, "Failed to finalize ${inc.displayName}")
            ConduitRuntime.upsertTransfer(
                TransferUi(inc.transferId, inc.displayName, sending = false, inc.received, inc.total, failed = true),
            )
            ConduitRuntime.lastEvent.value = "Couldn't save ${inc.displayName}"
            autoRemove(inc.transferId)
        }
    }

    /** Post a notification so the user knows the file arrived and can open Downloads. */
    private fun notifyReceived(name: String) {
        try {
            val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, "File transfers", NotificationManager.IMPORTANCE_DEFAULT)
                        .apply { description = "Files received from your PC" },
                )
            }
            val openDownloads = PendingIntent.getActivity(
                context, 0,
                Intent(DownloadManager.ACTION_VIEW_DOWNLOADS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE,
            )
            val notification = Notification.Builder(context, CHANNEL_ID)
                .setContentTitle("File received")
                .setContentText("$name saved to Downloads")
                .setSmallIcon(io.conduit.R.drawable.ic_stat_conduit)
                .setAutoCancel(true)
                .setContentIntent(openDownloads)
                .build()
            nm.notify(name.hashCode(), notification)
        } catch (e: Exception) {
            log.w(e, "Could not post file-received notification")
        }
    }

    // ---- Helpers --------------------------------------------------------------

    private fun queryName(uri: Uri): String {
        if (uri.scheme == "file") return uri.lastPathSegment ?: "conduit-file"
        context.contentResolver.query(uri, null, null, null, null)?.use { c ->
            val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (idx >= 0 && c.moveToFirst()) return c.getString(idx)
        }
        return uri.lastPathSegment ?: "conduit-file"
    }

    private fun querySize(uri: Uri): Long {
        if (uri.scheme == "file") return uri.path?.let { File(it).length() } ?: 0
        context.contentResolver.query(uri, null, null, null, null)?.use { c ->
            val idx = c.getColumnIndex(OpenableColumns.SIZE)
            if (idx >= 0 && c.moveToFirst()) return c.getLong(idx)
        }
        return 0
    }

    private fun uniqueFile(file: File): File {
        if (!file.exists()) return file
        val base = file.nameWithoutExtension
        val ext = file.extension.let { if (it.isEmpty()) "" else ".$it" }
        var i = 1
        while (true) {
            val candidate = File(file.parentFile, "$base ($i)$ext")
            if (!candidate.exists()) return candidate
            i++
        }
    }

    private companion object {
        const val CHANNEL_ID = "conduit_files"
    }
}
