package io.conduit.ui

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.TextView

/**
 * An on-screen keyboard laid out like a laptop's, for driving the PC while mirroring its desktop.
 * Unlike the Android soft keyboard (which only yields characters), this has a function row, real
 * modifier keys and arrows, so chords like Ctrl+C, Alt+Tab or Ctrl+Alt+Del come through.
 *
 * Modifiers (Ctrl/Alt/Shift/Win) are sticky: tap to arm for the next key, long-press to lock until
 * tapped off. When a "hard" modifier (Ctrl/Alt/Win) is armed, the next key goes out as
 * [Listener.onCombo]; otherwise printable keys are sent as [Listener.onText] (honoring Shift/Caps
 * locally, so the PC's layout doesn't matter) and the rest as named [Listener.onKey] presses.
 */
@SuppressLint("SetTextI18n", "ClickableViewAccessibility")
class PcKeyboardView(context: Context) : LinearLayout(context) {

    interface Listener {
        fun onText(text: String)
        fun onKey(key: String)
        fun onCombo(mods: String, key: String)
    }

    var listener: Listener? = null

    private val armed = linkedSetOf<String>()   // ctrl / alt / shift / win armed for the next key
    private val locked = mutableSetOf<String>() // subset of armed that stays until tapped off
    private var caps = false

    // A modifier can appear on more than one key (e.g. left/right Shift), so track all of them.
    private val modKeys = mutableMapOf<String, MutableList<TextView>>()
    private val capsKeys = mutableListOf<TextView>()

    private val density = resources.displayMetrics.density
    private fun dp(v: Int) = (v * density).toInt()

    init {
        orientation = VERTICAL
        setBackgroundColor(Color.argb(238, 8, 14, 22))
        setPadding(dp(3), dp(3), dp(3), dp(3))
        buildRows()
        refreshModVisuals()
    }

    // ---- layout -------------------------------------------------------------

    private fun buildRows() {
        // Function row.
        newRow().let { r ->
            addSpecial(r, "Esc", "escape", 1.6f)
            for (i in 1..12) addSpecial(r, "F$i", "f$i", 1f)
            addSpecial(r, "Del", "delete", 1.6f)
        }

        // Number row.
        newRow().let { r ->
            addChar(r, "`", "~")
            for ((b, s) in NUMBER_ROW) addChar(r, b, s)
            addChar(r, "-", "_")
            addChar(r, "=", "+")
            addSpecial(r, "⌫", "backspace", 2f)
        }

        // Top letter row.
        newRow().let { r ->
            addSpecial(r, "Tab", "tab", 2f)
            for (c in "qwertyuiop") addChar(r, c.toString(), c.uppercase())
            addChar(r, "[", "{"); addChar(r, "]", "}"); addChar(r, "\\", "|")
        }

        // Home row.
        newRow().let { r ->
            addCaps(r, 2f)
            for (c in "asdfghjkl") addChar(r, c.toString(), c.uppercase())
            addChar(r, ";", ":"); addChar(r, "'", "\"")
            addSpecial(r, "Enter", "enter", 2f)
        }

        // Bottom letter row.
        newRow().let { r ->
            addMod(r, "Shift", "shift", 2.5f)
            for (c in "zxcvbnm") addChar(r, c.toString(), c.uppercase())
            addChar(r, ",", "<"); addChar(r, ".", ">"); addChar(r, "/", "?")
            addMod(r, "Shift", "shift", 2.5f)
        }

        // Modifier + space + arrows.
        newRow().let { r ->
            addMod(r, "Ctrl", "ctrl", 1.7f)
            addMod(r, "Win", "win", 1.5f)
            addMod(r, "Alt", "alt", 1.5f)
            addSpecial(r, "Space", "space", 5f)
            addMod(r, "Alt", "alt", 1.5f)
            addSpecial(r, "◀", "left", 1f)
            addSpecial(r, "▲", "up", 1f)
            addSpecial(r, "▼", "down", 1f)
            addSpecial(r, "▶", "right", 1f)
        }
    }

    private fun newRow(): LinearLayout {
        val row = LinearLayout(context).apply { orientation = HORIZONTAL }
        addView(row, LayoutParams(LayoutParams.MATCH_PARENT, 0, 1f))
        return row
    }

    private fun keyView(label: String, weight: Float): TextView {
        val tv = TextView(context).apply {
            text = label
            gravity = Gravity.CENTER
            setTextColor(Color.argb(235, 225, 240, 245))
            textSize = 13f
            isClickable = true
            isFocusable = false
            background = keyBg(active = false)
        }
        tv.layoutParams = LayoutParams(0, LayoutParams.MATCH_PARENT, weight).apply {
            setMargins(dp(2), dp(2), dp(2), dp(2))
        }
        return tv
    }

    private fun keyBg(active: Boolean, locked: Boolean = false): GradientDrawable =
        GradientDrawable().apply {
            cornerRadius = dp(6).toFloat()
            when {
                locked -> { setColor(Color.argb(230, 0, 150, 172)); setStroke(dp(1), Color.argb(255, 0, 224, 244)) }
                active -> { setColor(Color.argb(160, 0, 120, 140)); setStroke(dp(1), Color.argb(210, 0, 200, 220)) }
                else   -> { setColor(Color.argb(255, 26, 34, 46)); setStroke(dp(1), Color.argb(70, 120, 160, 180)) }
            }
        }

    private fun addChar(row: LinearLayout, base: String, shifted: String, weight: Float = 1f) {
        val cap = base.length == 1 && base[0].isLetter()
        val tv = keyView(if (cap) base.uppercase() else base, weight)
        tv.setOnClickListener { pressChar(base, shifted) }
        row.addView(tv)
    }

    private fun addSpecial(row: LinearLayout, label: String, name: String, weight: Float) {
        val tv = keyView(label, weight)
        tv.setOnClickListener { pressSpecial(name) }
        row.addView(tv)
    }

    private fun addMod(row: LinearLayout, label: String, name: String, weight: Float) {
        val tv = keyView(label, weight)
        modKeys.getOrPut(name) { mutableListOf() }.add(tv)
        tv.setOnClickListener { toggleMod(name, lock = false) }
        tv.setOnLongClickListener { toggleMod(name, lock = true); true }
        row.addView(tv)
    }

    private fun addCaps(row: LinearLayout, weight: Float) {
        val tv = keyView("Caps", weight)
        capsKeys.add(tv)
        tv.setOnClickListener { caps = !caps; refreshModVisuals() }
        row.addView(tv)
    }

    // ---- key presses --------------------------------------------------------

    private fun pressChar(base: String, shifted: String) {
        val hard = armed.any { it != "shift" }   // Ctrl / Alt / Win change this into a chord
        if (hard) {
            listener?.onCombo(modString(), base)
        } else {
            val isLetter = base.length == 1 && base[0].isLetter()
            val out = when {
                isLetter        -> if (caps != ("shift" in armed)) base.uppercase() else base
                "shift" in armed -> shifted
                else            -> base
            }
            listener?.onText(out)
        }
        consumeMods()
    }

    private fun pressSpecial(name: String) {
        if (armed.isEmpty()) listener?.onKey(name)
        else listener?.onCombo(modString(), name)
        consumeMods()
    }

    /** Armed modifiers as a stable '+'-joined string the PC splits, e.g. "ctrl+shift". */
    private fun modString(): String =
        MOD_ORDER.filter { it in armed }.joinToString("+")

    private fun toggleMod(name: String, lock: Boolean) {
        if (lock) {
            if (name in locked) { locked.remove(name); armed.remove(name) }
            else { locked.add(name); armed.add(name) }
        } else {
            if (name in armed) { armed.remove(name); locked.remove(name) }
            else armed.add(name)
        }
        refreshModVisuals()
    }

    /** After a real key, drop any armed-but-not-locked modifiers (one-shot Shift/Ctrl behavior). */
    private fun consumeMods() {
        val transient = armed.filter { it !in locked }
        if (transient.isNotEmpty()) {
            armed.removeAll(transient.toSet())
            refreshModVisuals()
        }
    }

    private fun refreshModVisuals() {
        for ((name, views) in modKeys) {
            for (v in views) v.background = keyBg(active = name in armed, locked = name in locked)
        }
        for (v in capsKeys) v.background = keyBg(active = caps, locked = caps)
    }

    private companion object {
        val MOD_ORDER = listOf("ctrl", "alt", "shift", "win")
        val NUMBER_ROW = listOf(
            "1" to "!", "2" to "@", "3" to "#", "4" to "$", "5" to "%",
            "6" to "^", "7" to "&", "8" to "*", "9" to "(", "0" to ")",
        )
    }
}
