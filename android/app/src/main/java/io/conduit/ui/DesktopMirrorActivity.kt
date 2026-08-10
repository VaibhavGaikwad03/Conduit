package io.conduit.ui

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.MotionEvent
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.Toast
import io.conduit.logging.ConduitLog
import io.conduit.protocol.Packet
import io.conduit.protocol.PacketType
import io.conduit.runtime.ConduitRuntime
import org.json.JSONObject
import java.io.DataInputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.math.abs
import kotlin.math.min

/**
 * Full-screen viewer for the PC's desktop, with direct-touch control. Sends `desktop-start`, listens
 * on port 5466 for the PC's H.264 stream (the PC connects out to us), decodes it into a SurfaceView,
 * and turns touches into absolute `pc-input` packets: tap = click there, drag = press-move-release,
 * long-press = right-click, two-finger drag = scroll. The inverse of ScreenStreamer (which mirrors
 * this phone's screen to the PC).
 */
class DesktopMirrorActivity : Activity() {
    private val log = ConduitLog.tag("Desktop")

    companion object {
        const val EXTRA_DEVICE_ID = "deviceId"
        const val EXTRA_DEVICE_NAME = "deviceName"
        private const val PORT = 5466
        private const val MIME = MediaFormat.MIMETYPE_VIDEO_AVC
        private const val LONG_PRESS_MS = 450L
        private const val TOUCH_SLOP = 16f
        private const val SCROLL_STEP = 40f   // px per wheel notch
    }

    private var deviceId: String? = null
    private val ui = Handler(Looper.getMainLooper())

    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var keyInput: EditText

    @Volatile private var surface: Surface? = null
    private val surfaceLatch = CountDownLatch(1)
    @Volatile private var running = false

    private var serverSocket: ServerSocket? = null
    private var socket: Socket? = null
    private var codec: MediaCodec? = null
    private var receiver: Thread? = null

    // ---- lifecycle ----------------------------------------------------------

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        deviceId = intent.getStringExtra(EXTRA_DEVICE_ID)
        if (deviceId == null) { finish(); return }

        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        goImmersive()

        buildUi()
        setContentView(root)

        running = true
        startReceiver()
    }

    private fun buildUi() {
        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                surface = holder.surface
                surfaceLatch.countDown()
            }
            override fun surfaceChanged(holder: SurfaceHolder, f: Int, w: Int, h: Int) {}
            override fun surfaceDestroyed(holder: SurfaceHolder) { surface = null }
        })
        root.addView(surfaceView, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))
        surfaceView.setOnTouchListener { _, e -> onTouch(e); true }

        // Hidden field that captures soft-keyboard input to forward to the PC.
        keyInput = EditText(this).apply {
            alpha = 0f
            setSingleLine(false)
            imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN
        }
        wireKeyboard()
        root.addView(keyInput, FrameLayout.LayoutParams(1, 1))

        // Small keyboard toggle in the corner.
        val kbBtn = Button(this).apply {
            text = "⌨"
            setOnClickListener { toggleKeyboard() }
        }
        root.addView(kbBtn, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.END))
    }

    override fun onDestroy() {
        super.onDestroy()
        running = false
        stopDesktop()
        try { serverSocket?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        receiver?.interrupt()
    }

    private fun goImmersive() {
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_FULLSCREEN or
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
    }

    // ---- receive + decode ---------------------------------------------------

    private fun startReceiver() {
        receiver = thread(name = "desktop-recv") {
            try {
                val server = ServerSocket()
                server.reuseAddress = true
                server.bind(InetSocketAddress(PORT))
                server.soTimeout = 10_000
                serverSocket = server

                // Now that we're listening, ask the PC to connect and stream.
                startDesktop()

                val sock = server.accept().apply { tcpNoDelay = true }
                socket = sock
                val input = DataInputStream(sock.getInputStream())

                if (!surfaceLatch.await(5, TimeUnit.SECONDS)) {
                    log.w("Surface never became ready")
                    finishWithToast("Couldn't start the viewer")
                    return@thread
                }
                decodeLoop(input)
            } catch (e: java.net.SocketTimeoutException) {
                log.w("PC never connected for desktop mirror")
                finishWithToast("PC didn't start sharing its screen")
            } catch (e: Exception) {
                if (running) log.e(e, "Desktop receive failed")
            } finally {
                if (running) ui.post { finish() }
            }
        }
    }

    private fun decodeLoop(input: DataInputStream) {
        val info = MediaCodec.BufferInfo()
        var configured = false
        var ptsUs = 0L

        while (running) {
            val len = input.readInt()
            if (len <= 0) continue
            val au = ByteArray(len)
            input.readFully(au)

            if (!configured) {
                // The first access unit is an IDR carrying SPS/PPS; hand those to the codec as
                // codec-specific data, then feed the rest as the first frame.
                val (csd, frame) = splitCsd(au)
                val format = MediaFormat.createVideoFormat(MIME, 1920, 1080)
                if (csd != null) format.setByteBuffer("csd-0", ByteBuffer.wrap(csd))
                codec = MediaCodec.createDecoderByType(MIME).apply {
                    configure(format, surface, null, 0)
                    start()
                }
                configured = true
                feed(frame, ptsUs); ptsUs += 33_333
                drain(info)
                continue
            }

            feed(au, ptsUs); ptsUs += 33_333
            drain(info)
        }
    }

    private fun feed(au: ByteArray, ptsUs: Long) {
        val c = codec ?: return
        val index = c.dequeueInputBuffer(10_000)
        if (index >= 0) {
            val buf = c.getInputBuffer(index) ?: return
            buf.clear()
            buf.put(au)
            c.queueInputBuffer(index, 0, au.size, ptsUs, 0)
        }
    }

    private fun drain(info: MediaCodec.BufferInfo) {
        val c = codec ?: return
        var index = c.dequeueOutputBuffer(info, 0)
        while (index >= 0) {
            c.releaseOutputBuffer(index, true) // render to the surface
            index = c.dequeueOutputBuffer(info, 0)
        }
        if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
            val f = c.outputFormat
            fitSurface(f.getInteger(MediaFormat.KEY_WIDTH), f.getInteger(MediaFormat.KEY_HEIGHT))
        }
    }

    /** Resize the SurfaceView to the video's aspect so touch coords map 1:1 over the picture. */
    private fun fitSurface(vw: Int, vh: Int) {
        if (vw <= 0 || vh <= 0) return
        ui.post {
            val cw = root.width; val ch = root.height
            if (cw == 0 || ch == 0) return@post
            val scale = min(cw.toFloat() / vw, ch.toFloat() / vh)
            val lp = surfaceView.layoutParams as FrameLayout.LayoutParams
            lp.width = (vw * scale).toInt()
            lp.height = (vh * scale).toInt()
            lp.gravity = Gravity.CENTER
            surfaceView.layoutParams = lp
        }
    }

    /** Split an access unit into (SPS/PPS as csd, everything else). csd is null if no SPS is present. */
    private fun splitCsd(au: ByteArray): Pair<ByteArray?, ByteArray> {
        val starts = startCodes(au)
        if (starts.isEmpty()) return null to au
        val csd = ByteArray(au.size)
        var csdLen = 0
        val frame = ByteArray(au.size)
        var frameLen = 0
        for (i in starts.indices) {
            val begin = starts[i].first
            val end = if (i + 1 < starts.size) starts[i + 1].first else au.size
            val nalType = au[starts[i].second].toInt() and 0x1F
            if (nalType == 7 || nalType == 8) { // SPS / PPS
                System.arraycopy(au, begin, csd, csdLen, end - begin); csdLen += end - begin
            } else {
                System.arraycopy(au, begin, frame, frameLen, end - begin); frameLen += end - begin
            }
        }
        val csdOut = if (csdLen == 0) null else csd.copyOf(csdLen)
        val frameOut = if (frameLen == 0) au else frame.copyOf(frameLen)
        return csdOut to frameOut
    }

    /** Returns (startCodeOffset, nalHeaderOffset) for each Annex-B start code in the buffer. */
    private fun startCodes(d: ByteArray): List<Pair<Int, Int>> {
        val out = ArrayList<Pair<Int, Int>>()
        var i = 0
        while (i + 3 < d.size) {
            if (d[i] == 0.toByte() && d[i + 1] == 0.toByte() && d[i + 2] == 1.toByte()) {
                out.add(i to i + 3); i += 3
            } else if (d[i] == 0.toByte() && d[i + 1] == 0.toByte() &&
                d[i + 2] == 0.toByte() && d[i + 3] == 1.toByte()) {
                out.add(i to i + 4); i += 4
            } else i++
        }
        return out
    }

    // ---- touch -> pc-input --------------------------------------------------

    private var downX = 0f
    private var downY = 0f
    private var moved = false
    private var leftDown = false
    private var rightDone = false
    private var scrolling = false
    private var scrollAccum = 0f
    private var lastScrollY = 0f
    private var longPress: Runnable? = null

    private fun onTouch(e: MotionEvent) {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x; downY = e.y
                moved = false; leftDown = false; rightDone = false; scrolling = false
                val nx = nx(e.x); val ny = ny(e.y)
                pc { put("action", "moveabs"); put("x", nx); put("y", ny) }
                longPress = Runnable {
                    if (!moved && !scrolling) {
                        rightDone = true
                        pc { put("action", "tap"); put("x", nx(downX)); put("y", ny(downY)); put("button", "right") }
                    }
                }
                ui.postDelayed(longPress!!, LONG_PRESS_MS)
            }

            MotionEvent.ACTION_POINTER_DOWN -> { // second finger => scroll gesture
                cancelLongPress()
                if (leftDown) { pc { put("action", "up"); put("button", "left") }; leftDown = false }
                scrolling = true
                scrollAccum = 0f
                lastScrollY = avgY(e)
            }

            MotionEvent.ACTION_MOVE -> {
                if (scrolling && e.pointerCount >= 2) {
                    val y = avgY(e)
                    scrollAccum += y - lastScrollY
                    lastScrollY = y
                    while (abs(scrollAccum) >= SCROLL_STEP) {
                        // Natural direct-touch scrolling: dragging the content up (fingers up,
                        // negative dy) moves the view down = wheel down (negative notch), and
                        // vice-versa. This matches how the page follows your fingers on the phone.
                        val notch = if (scrollAccum > 0) 120 else -120
                        pc { put("action", "scroll"); put("amount", notch) }
                        scrollAccum -= if (scrollAccum > 0) SCROLL_STEP else -SCROLL_STEP
                    }
                } else if (!scrolling && e.pointerCount == 1) {
                    if (!moved && abs(e.x - downX) + abs(e.y - downY) > TOUCH_SLOP) {
                        moved = true
                        cancelLongPress()
                        if (!rightDone) { pc { put("action", "down"); put("button", "left") }; leftDown = true }
                    }
                    pc { put("action", "moveabs"); put("x", nx(e.x)); put("y", ny(e.y)) }
                }
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                cancelLongPress()
                if (leftDown) {
                    pc { put("action", "up"); put("button", "left") }
                    leftDown = false
                } else if (!rightDone && !scrolling && !moved) {
                    pc { put("action", "tap"); put("x", nx(e.x)); put("y", ny(e.y)); put("button", "left") }
                }
                scrolling = false
            }
        }
    }

    private fun avgY(e: MotionEvent): Float {
        var sum = 0f
        for (i in 0 until e.pointerCount) sum += e.getY(i)
        return sum / e.pointerCount
    }

    private fun nx(x: Float): Double =
        (x / surfaceView.width.coerceAtLeast(1)).toDouble().coerceIn(0.0, 1.0)

    private fun ny(y: Float): Double =
        (y / surfaceView.height.coerceAtLeast(1)).toDouble().coerceIn(0.0, 1.0)

    private fun cancelLongPress() { longPress?.let { ui.removeCallbacks(it) }; longPress = null }

    // ---- keyboard -----------------------------------------------------------

    private fun wireKeyboard() {
        keyInput.addTextChangedListener(object : TextWatcher {
            private var prev = ""
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) {
                val cur = s?.toString() ?: ""
                if (cur.length > prev.length) {
                    val added = cur.substring(prev.length)
                    for (ch in added) {
                        if (ch == '\n') pc { put("action", "key"); put("key", "enter") }
                        else pc { put("action", "text"); put("text", ch.toString()) }
                    }
                } else if (cur.length < prev.length) {
                    repeat(prev.length - cur.length) { pc { put("action", "key"); put("key", "backspace") } }
                }
                prev = cur
                // Keep the buffer from growing without bound.
                if (cur.length > 512) { keyInput.setText(""); prev = "" }
            }
        })
    }

    private fun toggleKeyboard() {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        keyInput.requestFocus()
        imm.toggleSoftInput(InputMethodManager.SHOW_FORCED, 0)
    }

    // ---- control packets ----------------------------------------------------

    private fun pc(build: JSONObject.() -> Unit) {
        val id = deviceId ?: return
        ConduitRuntime.node?.sendTo(id, Packet.create(PacketType.PC_INPUT, build))
    }

    private fun startDesktop() {
        val id = deviceId ?: return
        ConduitRuntime.node?.sendTo(id, Packet.create(PacketType.DESKTOP_START) { put("port", PORT) })
    }

    private fun stopDesktop() {
        val id = deviceId ?: return
        ConduitRuntime.node?.sendTo(id, Packet.create(PacketType.DESKTOP_STOP))
    }

    private fun finishWithToast(msg: String) {
        ui.post {
            Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
            finish()
        }
    }
}
