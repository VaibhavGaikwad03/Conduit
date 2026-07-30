package io.conduit.features

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import androidx.core.content.ContextCompat
import io.conduit.logging.ConduitLog
import io.conduit.service.ConduitService
import java.io.DataOutputStream
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Streams the phone's camera to the PC as H.264 so it can appear there as a webcam.
 * The camera renders straight into the encoder's input Surface (no manual YUV copy),
 * the encoder's H.264 output is sent length-prefixed over a dedicated TCP socket, and
 * the PC decodes it into its virtual camera. Uses Camera2 directly so it can run from
 * the foreground service without a UI LifecycleOwner.
 */
class WebcamStreamer(private val context: Context) {
    private val log = ConduitLog.tag("Webcam")

    private companion object {
        const val WIDTH = 1280
        const val HEIGHT = 720
        const val FPS = 30
        const val BITRATE = 4_000_000
    }

    @Volatile private var running = false
    private var encoder: MediaCodec? = null
    private var inputSurface: Surface? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var socket: Socket? = null
    private var out: DataOutputStream? = null
    private var cameraThread: HandlerThread? = null
    private var cameraHandler: Handler? = null

    val isRunning get() = running

    fun start(host: String, port: Int) {
        if (running) return
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED) {
            log.w("Camera permission not granted; cannot start webcam")
            return
        }
        try {
            // Android 14+ blocks camera access from a plain dataSync foreground service, so add the
            // camera type to the running service before opening the camera (removed again in stop()).
            ConduitService.instance?.setCameraActive(true)

            socket = Socket(host, port).apply { tcpNoDelay = true }
            out = DataOutputStream(socket!!.getOutputStream())

            encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).also { codec ->
                val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, WIDTH, HEIGHT).apply {
                    setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                    setInteger(MediaFormat.KEY_BIT_RATE, BITRATE)
                    setInteger(MediaFormat.KEY_FRAME_RATE, FPS)
                    setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1) // keyframe each second
                }
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                inputSurface = codec.createInputSurface()
                codec.start()
            }

            running = true
            thread(name = "webcam-drain") { drainLoop() }
            openCamera()
            log.i("Webcam streaming to $host:$port")
        } catch (e: Exception) {
            log.e(e, "Failed to start webcam stream")
            stop()
        }
    }

    private fun openCamera() {
        val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val cameraId = pickFrontCamera(manager)
        cameraThread = HandlerThread("webcam-camera").apply { start() }
        cameraHandler = Handler(cameraThread!!.looper)

        try {
            manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    camera = device
                    startSession(device)
                }
                override fun onDisconnected(device: CameraDevice) { device.close() }
                override fun onError(device: CameraDevice, error: Int) {
                    log.e(RuntimeException("camera error $error"), "Camera open error")
                    device.close()
                }
            }, cameraHandler)
        } catch (e: SecurityException) {
            log.e(e, "Camera permission missing at open")
            stop()
        }
    }

    private fun startSession(device: CameraDevice) {
        val surface = inputSurface ?: return
        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
            addTarget(surface)
        }
        @Suppress("DEPRECATION")
        device.createCaptureSession(listOf(surface), object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(configured: CameraCaptureSession) {
                session = configured
                configured.setRepeatingRequest(request.build(), null, cameraHandler)
            }
            override fun onConfigureFailed(configured: CameraCaptureSession) {
                log.e(RuntimeException("session config failed"), "Capture session failed")
                stop()
            }
        }, cameraHandler)
    }

    private fun pickFrontCamera(manager: CameraManager): String {
        val ids = manager.cameraIdList
        // Prefer the front camera (selfie) for a webcam; fall back to whatever exists.
        for (id in ids) {
            val facing = manager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING)
            if (facing == CameraCharacteristics.LENS_FACING_FRONT) return id
        }
        return ids.firstOrNull() ?: throw IllegalStateException("No camera available")
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
            log.w(e, "Video socket write failed; stopping")
            running = false
        }
    }

    fun stop() {
        running = false
        try { session?.close() } catch (_: Exception) {}
        try { camera?.close() } catch (_: Exception) {}
        try { encoder?.stop(); encoder?.release() } catch (_: Exception) {}
        try { inputSurface?.release() } catch (_: Exception) {}
        try { out?.close(); socket?.close() } catch (_: Exception) {}
        cameraThread?.quitSafely()
        session = null; camera = null; encoder = null; inputSurface = null
        out = null; socket = null; cameraThread = null; cameraHandler = null
        ConduitService.instance?.setCameraActive(false)
        log.i("Webcam streaming stopped")
    }
}
