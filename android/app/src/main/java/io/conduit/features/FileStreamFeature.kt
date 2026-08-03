package io.conduit.features

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import io.conduit.logging.ConduitLog
import io.conduit.model.Ports
import io.conduit.network.ConduitNode
import io.conduit.network.FrameCodec
import io.conduit.network.SessionCipher
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime
import io.conduit.runtime.TransferUi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * The fast path for large files: raw AES-256-GCM blocks over a dedicated port
 * ([Ports.FILE_STREAM]) instead of base64 chunks in the JSON session — no base64, no per-chunk
 * JSON, far fewer allocations (so no GC thrash on big files). A `file-offer` with `stream:true`
 * over the encrypted session announces the transfer; the raw stream is bound to it by transferId
 * and encrypted with the same per-peer key, so it stays end-to-end secure. Mirrors the Windows
 * FileStreamService.
 */
class FileStreamFeature(private val context: Context, private val node: ConduitNode) {
    private val log = ConduitLog.tag("FileStream")
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: ServerSocket? = null

    // Offers announced (file-offer{stream}) whose raw stream hasn't connected yet, keyed by transferId.
    private val pending = ConcurrentHashMap<String, Pending>()

    private data class Pending(val senderDeviceId: String, val name: String, val size: Long)

    /** True when we have what we need (key + IP) to stream to this peer. */
    fun canStream(deviceId: String): Boolean =
        node.sessionKeyFor(deviceId) != null && node.ipFor(deviceId) != null

    fun start() {
        val s = ServerSocket().apply {
            reuseAddress = true
            bind(InetSocketAddress(Ports.FILE_STREAM))
        }
        server = s
        scope.launch { acceptLoop(s) }
        log.i("File stream listener started on ${Ports.FILE_STREAM}")
    }

    fun stop() {
        runCatching { server?.close() }
    }

    /** A peer announced (via file-offer{stream}) that it's about to stream this file to us. */
    fun registerIncoming(transferId: String, senderDeviceId: String, name: String, size: Long) {
        if (transferId.isEmpty()) return
        val cleanName = File(name).name
        pending[transferId] = Pending(senderDeviceId, cleanName, size)
        ConduitRuntime.upsertTransfer(TransferUi(transferId, cleanName, sending = false, 0, size))
        // If the raw stream never arrives, don't leave the UI stuck at 0%.
        scope.launch {
            delay(20_000)
            if (pending.remove(transferId) != null) {
                log.w("Stream for $cleanName never arrived")
                ConduitRuntime.upsertTransfer(TransferUi(transferId, cleanName, sending = false, 0, size, failed = true))
                autoRemove(transferId)
            }
        }
    }

    // ---- Sending --------------------------------------------------------------

    /** Announces the file, then streams its bytes raw + encrypted to the peer's stream port. */
    fun sendFile(deviceId: String, uri: Uri, name: String, size: Long) {
        scope.launch {
            val key = node.sessionKeyFor(deviceId)
            val ip = node.ipFor(deviceId)
            if (key == null || ip == null) {
                log.w("No key/ip for $deviceId; can't stream $name")
                return@launch
            }
            val transferId = UUID.randomUUID().toString().replace("-", "")
            ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, 0, size))
            log.i("Streaming $name ($size bytes) to $deviceId")

            node.sendTo(deviceId, Packet.create(PacketType.FILE_OFFER) {
                put("transferId", transferId); put("name", name)
                put("size", size); put("mime", "application/octet-stream")
                put("stream", true)
            })

            try {
                Socket().use { sock ->
                    sock.connect(InetSocketAddress(ip, Ports.FILE_STREAM), 5000)
                    sock.sendBufferSize = 1 shl 20
                    val out = BufferedOutputStream(sock.getOutputStream())
                    FrameCodec.writeFrame(out, MAGIC)
                    FrameCodec.writeFrame(out, node.self.deviceId.toByteArray())
                    FrameCodec.writeFrame(out, transferId.toByteArray())

                    val cipher = SessionCipher(key)
                    val input = context.contentResolver.openInputStream(uri)
                        ?: throw java.io.IOException("Can't open $uri")
                    BufferedInputStream(input).use { stream ->
                        val buf = ByteArray(BLOCK)
                        var sent = 0L
                        var lastPct = -1
                        while (true) {
                            val n = readBlock(stream, buf)
                            if (n <= 0) break
                            val plain = if (n == buf.size) buf else buf.copyOf(n)
                            FrameCodec.writeFrame(out, cipher.encrypt(plain))
                            sent += n
                            val pct = if (size > 0) ((sent * 100) / size).toInt() else 0
                            if (pct != lastPct) {
                                lastPct = pct
                                ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, sent, size))
                            }
                        }
                        FrameCodec.writeFrame(out, ByteArray(0)) // end of stream
                        out.flush()
                    }
                }
                ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, size, size, done = true))
                autoRemove(transferId)
                log.i("Finished streaming $name")
            } catch (e: Exception) {
                log.e(e, "Failed streaming $name")
                ConduitRuntime.upsertTransfer(TransferUi(transferId, name, sending = true, 0, 0, failed = true))
                autoRemove(transferId)
            }
        }
    }

    // ---- Receiving ------------------------------------------------------------

    private fun acceptLoop(s: ServerSocket) {
        while (scope.isActive) {
            try {
                val client = s.accept()
                scope.launch { handleIncoming(client) }
            } catch (e: Exception) {
                if (scope.isActive) log.w(e, "File stream accept error")
            }
        }
    }

    private suspend fun handleIncoming(socket: Socket) {
        var out: OutputStream? = null
        var uri: Uri? = null
        var legacy: File? = null
        try {
            socket.use { sock ->
                sock.receiveBufferSize = 1 shl 20
                val input = BufferedInputStream(sock.getInputStream())
                val magic = FrameCodec.readFrame(input)
                if (magic == null || !magic.contentEquals(MAGIC)) return
                val senderId = String(FrameCodec.readFrame(input) ?: return)
                val transferId = String(FrameCodec.readFrame(input) ?: return)

                val p = waitForOffer(transferId) ?: run {
                    log.w("Stream for unknown transfer $transferId — dropping")
                    return
                }
                pending.remove(transferId)

                val key = node.sessionKeyFor(senderId) ?: run {
                    log.w("No key for stream sender $senderId"); return
                }
                val cipher = SessionCipher(key)

                // Open a Downloads sink (MediaStore on Q+, a public file on older Android).
                val mime = "application/octet-stream"
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    val values = ContentValues().apply {
                        put(MediaStore.Downloads.DISPLAY_NAME, p.name)
                        put(MediaStore.Downloads.MIME_TYPE, mime)
                        put(MediaStore.Downloads.IS_PENDING, 1)
                    }
                    uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                        ?: throw java.io.IOException("MediaStore insert returned null")
                    out = context.contentResolver.openOutputStream(uri!!)
                        ?: throw java.io.IOException("openOutputStream returned null")
                } else {
                    @Suppress("DEPRECATION")
                    val dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
                    dir.mkdirs()
                    legacy = uniqueFile(File(dir, p.name))
                    out = FileOutputStream(legacy)
                }
                val sink = BufferedOutputStream(out!!)
                log.i("Receiving stream ${p.name} (${p.size} bytes) → Downloads")

                var received = 0L
                var lastPct = -1
                while (true) {
                    val frame = FrameCodec.readFrame(input) ?: break // socket closed
                    if (frame.isEmpty()) break                        // end marker
                    val plain = cipher.decrypt(frame)
                    sink.write(plain)
                    received += plain.size
                    val pct = if (p.size > 0) ((received * 100) / p.size).toInt() else 0
                    if (pct != lastPct) {
                        lastPct = pct
                        ConduitRuntime.upsertTransfer(TransferUi(transferId, p.name, sending = false, received, p.size))
                    }
                }
                sink.flush()
                out!!.close()
                out = null

                val ok = p.size <= 0 || received == p.size
                if (ok) {
                    publish(uri, legacy)
                    log.i("Stream complete → Downloads/${p.name}")
                    ConduitRuntime.upsertTransfer(TransferUi(transferId, p.name, sending = false, p.size, p.size, done = true))
                    ConduitRuntime.lastEvent.value = "Saved ${p.name} to Downloads"
                } else {
                    log.w("Stream truncated for ${p.name} ($received/${p.size})")
                    discard(uri, legacy)
                    ConduitRuntime.upsertTransfer(TransferUi(transferId, p.name, sending = false, received, p.size, failed = true))
                    ConduitRuntime.lastEvent.value = "Couldn't download ${p.name}"
                }
                autoRemove(transferId)
            }
        } catch (e: Exception) {
            log.e(e, "File stream receive failed")
            runCatching { out?.close() }
            discard(uri, legacy)
        }
    }

    private suspend fun waitForOffer(transferId: String): Pending? {
        repeat(50) { // up to ~5s — the offer may race the stream connection
            pending[transferId]?.let { return it }
            delay(100)
        }
        return null
    }

    private fun publish(uri: Uri?, legacy: File?) {
        if (uri != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val values = ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }
            runCatching { context.contentResolver.update(uri, values, null, null) }
        } else if (legacy != null) {
            runCatching {
                android.media.MediaScannerConnection.scanFile(context, arrayOf(legacy.absolutePath), null, null)
            }
        }
    }

    private fun discard(uri: Uri?, legacy: File?) {
        runCatching { if (uri != null) context.contentResolver.delete(uri, null, null) }
        runCatching { legacy?.delete() }
    }

    private fun autoRemove(id: String) {
        scope.launch { delay(4000); ConduitRuntime.removeTransfer(id) }
    }

    private fun readBlock(input: InputStream, buf: ByteArray): Int {
        var off = 0
        while (off < buf.size) {
            val n = input.read(buf, off, buf.size - off)
            if (n < 0) break
            off += n
        }
        return off
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
        const val BLOCK = 1 * 1024 * 1024 // 1 MB plaintext per encrypted block
        val MAGIC = "CFS1".toByteArray(Charsets.US_ASCII)
    }
}
