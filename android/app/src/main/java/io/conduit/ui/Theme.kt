package io.conduit.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// ---- Brand palette (matches the Conduit logo + the Windows app) ----
val Cyan = Color(0xFF38CDEC)
val Blue = Color(0xFF4F8CFF)
val NavyBg = Color(0xFF0A1622)
val NavyBg2 = Color(0xFF0D1D2C)
val Card = Color(0xFF15293B)
val CardHi = Color(0xFF1D3A54)
val Stroke = Color(0xFF243F58)
val StrokeSoft = Color(0xFF1B3145)
val TextHi = Color(0xFFEDF3F8)
val TextMuted = Color(0xFF93A6B9)
val Faint = Color(0xFF6B7F93)
val Success = Color(0xFF34D399)
val Warn = Color(0xFFF5B44C)

private val ConduitColors = darkColorScheme(
    primary = Cyan,
    onPrimary = Color(0xFF04222E),
    secondary = Blue,
    onSecondary = Color(0xFF04102A),
    background = NavyBg,
    onBackground = TextHi,
    surface = Card,
    onSurface = TextHi,
    surfaceVariant = CardHi,
    onSurfaceVariant = TextMuted,
    outline = Stroke,
    error = Color(0xFFFF6B6B),
)

@Composable
fun ConduitTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = ConduitColors,
        typography = Typography(),
        content = content,
    )
}
