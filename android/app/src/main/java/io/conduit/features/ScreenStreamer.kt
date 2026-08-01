package io.conduit.features

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.DisplayMetrics
import android.view.Surface
import android.view.WindowManager
import io.conduit.logging.ConduitLog
import io.conduit.service.ConduitService
import java.io.DataOutputStream
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Mirrors the phone screen to the PC as H.264. MediaProjection renders the display straight into
 * the encoder's input Surface (via a VirtualDisplay — no manual pixel copy), the encoder's output
 * is sent length-prefixed over a dedicated TCP socket, and the PC decodes it into a window. Shares
 * the same wire format as the webcam feature; the PC just displays it instead of exposing a camera.
 */
class ScreenStreamer(private val context: Context) {
    private val log = ConduitLog.tag("Screen")

    private companion object {
        const val FPS = 30
        const val BITRATE = 8_000_000
        const val MAX_DIM = 1280 // cap the long edge to keep latency/bitrate sane
    }

    @Volatile private var running = false
    private var host: String? = null
    private var port: Int = 0

    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null
    private var socket: Socket? = null
    private var out: DataOutputStream? = null
    private var handlerThread: HandlerThread? = null
    private var handler: Handler? = null

    val isRunning get() = running

    /** Remember where to stream; the consent dialog supplies the projection token next. */
    fun prepare(host: String, port: Int) {
        this.host = host
        this.port = port
    }

    /** Handles the result of the system screen-capture consent dialog. */
    fun onPermissionResult(resultCode: Int, data: Intent?) {
        if (resultCode != Activity.RESULT_OK || data == null) {
            log.w("Screen capture consent denied")
            return
        }
        val h = host
        if (h == null) {
            log.w("No target host prepared for screen mirror")
            return
        }
        // This runs on the main thread (the consent activity's result callback), but start() opens
        // a TCP socket — do it off the main thread to avoid NetworkOnMainThreadException.
        thread(name = "screen-start") { start(h, port, resultCode, data) }
    }

    private fun start(host: String, port: Int, resultCode: Int, data: Intent) {
        if (running) return
        try {
            // Android 14+ requires the mediaProjection FGS type to be active before the projection
            // is granted, so promote the service first (dropped again in stop()).
            ConduitService.instance?.setMediaProjectionActive(true)

            val mpm = context.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val proj = mpm.getMediaProjection(resultCode, data)
                ?: throw IllegalStateException("getMediaProjection returned null")
            projection = proj

            handlerThread = HandlerThread("screen-capture").apply { start() }
            handler = Handler(handlerThread!!.looper)
            // Registering a callback is mandatory on Android 14+ before createVirtualDisplay.
            proj.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() { stop() }
            }, handler)

            val (w, h, dpi) = screenSize()

            socket = Socket(host, port).apply { tcpNoDelay = true }
            out = DataOutputStream(socket!!.getOutputStream())

            encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).also { codec ->
                val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h).apply {
                    setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                    setInteger(MediaFormat.KEY_BIT_RATE, BITRATE)
                    setInteger(MediaFormat.KEY_FRAME_RATE, FPS)
                    setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
                    setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
                    if (Build.VERSION.SDK_INT >= 29) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
                    if (Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LATENCY, 1)
                }
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                inputSurface = codec.createInputSurface()
                codec.start()
            }

            virtualDisplay = proj.createVirtualDisplay(
                "conduit-screen", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC,
                inputSurface, null, handler,
            )

            running = true
            thread(name = "screen-drain") { drainLoop() }
            log.i("Screen mirroring to $host:$port at ${w}x$h")
        } catch (e: Exception) {
            log.e(e, "Failed to start screen mirror")
            stop()
        }
    }

    /** Real display size scaled so the long edge is <= MAX_DIM, both dimensions even. */
    private fun screenSize(): Triple<Int, Int, Int> {
        val metrics = DisplayMetrics()
        var w: Int
        var h: Int
        val dpi: Int
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val wm = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
            val bounds = wm.maximumWindowMetrics.bounds
            w = bounds.width()
            h = bounds.height()
            dpi = context.resources.configuration.densityDpi
        } else {
            @Suppress("DEPRECATION")
            (context.getSystemService(Context.WINDOW_SERVICE) as WindowManager).defaultDisplay
                .getRealMetrics(metrics)
            w = metrics.widthPixels
            h = metrics.heightPixels
            dpi = metrics.densityDpi
        }
        val longEdge = maxOf(w, h)
        if (longEdge > MAX_DIM) {
            val scale = MAX_DIM.toFloat() / longEdge
            w = (w * scale).toInt()
            h = (h * scale).toInt()
        }
        // Round to a multiple of 16. The H.264 encoder pads the coded frame up to 16-pixel
        // macroblocks; any padding columns/rows are uninitialized and show up on the PC as a
        // green edge. Matching 16 means coded size == display size, so there's no padding.
        return Triple((w / 16) * 16, (h / 16) * 16, dpi)
    }

    private fun drainLoop() {
        val codec = encoder ?: return
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val index = codec.dequeueOutputBuffer(info, 10_000)
                if (index >= 0) {
                    val buffer = codec.getOutputBuffer(index)
                    if (buffer != null && info.size > 0) {
                        buffer.position(info.offset)
                        buffer.limit(info.offset + info.size)
                        val frame = ByteArray(info.size)
                        buffer.get(frame)
                        sendFrame(frame)
                    }
                    codec.releaseOutputBuffer(index, false)
                }
            }
        } catch (e: Exception) {
            if (running) log.w(e, "Encoder drain ended")
        }
    }

    @Synchronized
    private fun sendFrame(frame: ByteArray) {
        val stream = out ?: return
        try {
            stream.writeInt(frame.size) // 4-byte big-endian length prefix
            stream.write(frame)
            stream.flush()
        } catch (e: Exception) {
            log.w(e, "Screen socket write failed; stopping")
            running = false
        }
    }

    fun stop() {
        if (!running && projection == null) return
        running = false
        try { virtualDisplay?.release() } catch (_: Exception) {}
        try { encoder?.stop(); encoder?.release() } catch (_: Exception) {}
        try { inputSurface?.release() } catch (_: Exception) {}
        try { projection?.stop() } catch (_: Exception) {}
        try { out?.close(); socket?.close() } catch (_: Exception) {}
        handlerThread?.quitSafely()
        virtualDisplay = null; encoder = null; inputSurface = null; projection = null
        out = null; socket = null; handlerThread = null; handler = null
        ConduitService.instance?.setMediaProjectionActive(false)
        log.i("Screen mirroring stopped")
    }
}
