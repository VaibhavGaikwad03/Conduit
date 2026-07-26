package io.conduit.network

import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/**
 * Length-prefixed framing: [4-byte big-endian length][payload]. Matches the .NET FrameCodec.
 */
object FrameCodec {
    const val MAX_FRAME_SIZE = 16 * 1024 * 1024

    fun writeFrame(out: OutputStream, payload: ByteArray) {
        require(payload.size <= MAX_FRAME_SIZE) { "Frame too large: ${payload.size}" }
        val len = payload.size
        val header = byteArrayOf(
            (len ushr 24).toByte(),
            (len ushr 16).toByte(),
            (len ushr 8).toByte(),
            len.toByte(),
        )
        synchronized(out) {
            out.write(header)
            out.write(payload)
            out.flush()
        }
    }

    /** Reads one full frame, or returns null on clean end-of-stream. */
    fun readFrame(input: InputStream): ByteArray? {
        val din = if (input is DataInputStream) input else DataInputStream(input)
        val header = ByteArray(4)
        val first = input.read()
        if (first == -1) return null
        header[0] = first.toByte()
        readExact(din, header, 1, 3)

        val len = ((header[0].toInt() and 0xFF) shl 24) or
            ((header[1].toInt() and 0xFF) shl 16) or
            ((header[2].toInt() and 0xFF) shl 8) or
            (header[3].toInt() and 0xFF)
        if (len < 0 || len > MAX_FRAME_SIZE) throw IllegalStateException("Bad frame size $len")
        if (len == 0) return ByteArray(0)

        val payload = ByteArray(len)
        readExact(din, payload, 0, len)
        return payload
    }

    private fun readExact(din: DataInputStream, buf: ByteArray, offset: Int, count: Int) {
        var read = 0
        while (read < count) {
            val n = din.read(buf, offset + read, count - read)
            if (n == -1) throw EOFException("Stream ended mid-frame")
            read += n
        }
    }
}
