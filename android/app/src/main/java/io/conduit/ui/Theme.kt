package io.conduit.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val Accent = Color(0xFF4F8CFF)

private val DarkColors = darkColorScheme(
    primary = Accent,
    background = Color(0xFF1E1F26),
    surface = Color(0xFF282A36),
    onBackground = Color(0xFFF0F0F5),
    onSurface = Color(0xFFF0F0F5),
)

private val LightColors = lightColorScheme(
    primary = Accent,
    background = Color(0xFFF6F7FB),
    surface = Color(0xFFFFFFFF),
)

@Composable
fun ConduitTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) DarkColors else LightColors,
        content = content,
    )
}
