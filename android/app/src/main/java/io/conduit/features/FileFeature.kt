package io.conduit.features

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Base64
import io.conduit.logging.ConduitLog
import io.conduit.network.ConduitNode
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Sends and receives files as base64 chunks (file-offer → file-chunk* → file-complete).
 * Incoming files land in the app's external Downloads directory.
 */
class FileFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("FileTransfer")
    private val scope = CoroutineScope(Dispatchers.IO)
    private val incoming = ConcurrentHashMap<String, Incoming>()

    private val downloadDir: File
        get() = File(context.getExternalFilesDir(null), "ConduitDownloads").apply { mkdirs() }

    private class Incoming(val file: File, val out: FileOutputStream) {
        var nextSeq = 0
        val pending = sortedMapOf<Int, ByteArray>()
    }

    // ---- Sending --------------------------------------------------------------

    fun sendFile(deviceId: String, uri: Uri) {
        scope.launch {
            try {
                val name = queryName(uri)
                val size = querySize(uri)
                val transferId = UUID.randomUUID().toString().replace("-", "")
                log.i("Sending $name ($size bytes) to $deviceId")

                node.sendTo(deviceId, Packet.create(PacketType.FILE_OFFER) {
                    put("transferId", transferId); put("name", name)
                    put("size", size); put("mime", "application/octet-stream")
                })

                val sha = MessageDigest.getInstance("SHA-256")
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
                    }
                }
                val hash = sha.digest().joinToString("") { "%02x".format(it) }
                node.sendTo(deviceId, Packet.create(PacketType.FILE_COMPLETE) {
                    put("transferId", transferId); put("ok", true); put("sha256", hash)
                })
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
        val dest = uniqueFile(File(downloadDir, name))
        incoming[transferId] = Incoming(dest, FileOutputStream(dest))
        log.i("Receiving $name → ${dest.absolutePath}")
    }

    private fun chunk(packet: Packet) {
        val transferId = packet.getString("transferId") ?: return
        val inc = incoming[transferId] ?: return
        val seq = packet.getInt("seq")
        val data = Base64.decode(packet.getString("dataB64") ?: "", Base64.NO_WRAP)
        inc.pending[seq] = data
        while (inc.pending.containsKey(inc.nextSeq)) {
            inc.out.write(inc.pending.remove(inc.nextSeq)!!)
            inc.nextSeq++
        }
    }

    private fun complete(packet: Packet) {
        val transferId = packet.getString("transferId") ?: return
        val inc = incoming.remove(transferId) ?: return
        inc.out.flush(); inc.out.close()
        log.i("Received file OK: ${inc.file.absolutePath}")
    }

    // ---- Helpers --------------------------------------------------------------

    private fun queryName(uri: Uri): String {
        context.contentResolver.query(uri, null, null, null, null)?.use { c ->
            val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (idx >= 0 && c.moveToFirst()) return c.getString(idx)
        }
        return uri.lastPathSegment ?: "conduit-file"
    }

    private fun querySize(uri: Uri): Long {
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
}
