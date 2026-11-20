package com.unupunct.fleetmonitor.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

object FleetColors {
    val Bg = Color(0xFF0F1115)
    val Panel = Color(0xFF171A21)
    val PanelAlt = Color(0xFF1C2029)
    val Line = Color(0xFF2A2F3A)
    val Fg = Color(0xFFE6E8EC)
    val Muted = Color(0xFF8B93A1)
    val Accent = Color(0xFF4DA3FF)
    val AccentDim = Color(0xFF1F3A5C)
    val Good = Color(0xFF3DDC84)
    val Warn = Color(0xFFFFB020)
    val WarnBg = Color(0xFF3A2D10)
    val Bad = Color(0xFFFF5C5C)
}

private val Scheme = darkColorScheme(
    primary = FleetColors.Accent, onPrimary = Color(0xFF00213F),
    secondary = FleetColors.Good, background = FleetColors.Bg, onBackground = FleetColors.Fg,
    surface = FleetColors.Panel, onSurface = FleetColors.Fg, surfaceVariant = FleetColors.PanelAlt, onSurfaceVariant = FleetColors.Muted,
    outline = FleetColors.Line, error = FleetColors.Bad,
)

@Composable
fun FleetTheme(content: @Composable () -> Unit) = MaterialTheme(colorScheme = Scheme, content = content)
