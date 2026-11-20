package com.unupunct.fleetmonitor.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.unupunct.fleetmonitor.domain.DevicePresence
import com.unupunct.fleetmonitor.data.ObservationEntity
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.cos
import kotlin.math.sin

fun fmtTime(ms: Long?, pattern: String = "HH:mm:ss"): String = if (ms == null) "-" else SimpleDateFormat(pattern, Locale.getDefault()).format(Date(ms))

fun ago(ms: Long?, now: Long = System.currentTimeMillis()): String {
    if (ms == null) return "never"
    val s = ((now - ms) / 1000).coerceAtLeast(0)
    return when {
        s < 60 -> "$s s ago"
        s < 3600 -> "${s / 60} min ago"
        s < 86400 -> "${s / 3600} h ago"
        else -> "${s / 86400} d ago"
    }
}

fun presenceColor(p: DevicePresence) = when (p) {
    DevicePresence.Detected -> FleetColors.Good
    DevicePresence.Missing -> FleetColors.Warn
    DevicePresence.Uncertain -> FleetColors.Muted
}

fun presenceText(p: DevicePresence) = when (p) {
    DevicePresence.Detected -> "Detected"
    DevicePresence.Missing -> "Not heard recently"
    DevicePresence.Uncertain -> "Uncertain"
}

@Composable
fun Banner(title: String, text: String, modifier: Modifier = Modifier) {
    Column(
        modifier.fillMaxWidth().background(FleetColors.WarnBg, RoundedCornerShape(8.dp)).border(1.dp, FleetColors.Warn, RoundedCornerShape(8.dp)).padding(12.dp),
    ) {
        Text(title, color = FleetColors.Warn, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(4.dp))
        Text(text, color = FleetColors.Fg, fontSize = 13.sp)
    }
}

@Composable
fun Card2(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier.fillMaxWidth().background(FleetColors.Panel, RoundedCornerShape(10.dp)).border(1.dp, FleetColors.Line, RoundedCornerShape(10.dp)).padding(12.dp),
        content = content,
    )
}

@Composable
fun Label(text: String) = Text(text, color = FleetColors.Muted, fontSize = 12.sp)

/** Small trace of the latest RSSI samples. */
@Composable
fun Sparkline(samples: List<Int>, modifier: Modifier = Modifier, color: Color = FleetColors.Accent) {
    Canvas(modifier) {
        if (samples.size < 2) return@Canvas
        val path = Path()
        samples.forEachIndexed { i, v ->
            val x = size.width * i / (samples.size - 1)
            val y = size.height * (1f - ((v.coerceIn(-100, -30) + 100) / 70f))
            if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }
        drawPath(path, color, style = Stroke(width = 3f))
    }
}

class ChartPoint(val timeMs: Long, val avg: Double, val min: Int, val max: Int)

/** RSSI over time. Long silences are drawn as breaks: no line is drawn across time when nothing was heard. */
@Composable
fun RssiChart(points: List<ChartPoint>, fromMs: Long, toMs: Long, breakAfterMs: Long, modifier: Modifier = Modifier) {
    Canvas(modifier.background(FleetColors.Panel, RoundedCornerShape(8.dp))) {
        val left = 44f; val bottom = size.height - 8f; val top = 8f; val right = size.width - 8f
        fun y(v: Double) = (top + (bottom - top) * (1f - ((v.coerceIn(-100.0, -30.0) + 100) / 70f))).toFloat()
        fun x(t: Long) = (left + (right - left) * ((t - fromMs).toFloat() / (toMs - fromMs).coerceAtLeast(1))).coerceIn(left, right)
        for (db in listOf(-30, -50, -70, -90)) {
            drawLine(FleetColors.Line, Offset(left, y(db.toDouble())), Offset(right, y(db.toDouble())), 1f)
        }
        if (points.isEmpty()) return@Canvas
        val avg = Path()
        var prev: ChartPoint? = null
        for (p in points) {
            val px = x(p.timeMs)
            drawLine(FleetColors.Line.copy(alpha = 0.9f), Offset(px, y(p.min.toDouble())), Offset(px, y(p.max.toDouble())), 2f)
            if (prev == null || p.timeMs - prev.timeMs > breakAfterMs) avg.moveTo(px, y(p.avg)) else avg.lineTo(px, y(p.avg))
            prev = p
        }
        drawPath(avg, FleetColors.Accent, style = Stroke(width = 3f))
    }
}

/** Radar of nearby devices. Distance from the centre is RELATIVE signal strength only; the angle is arbitrary. */
@Composable
fun Radar(
    devices: List<DeviceRow>,
    selected: String?,
    onSelect: (String?) -> Unit,
    modifier: Modifier = Modifier,
) {
    Canvas(
        modifier.pointerInput(devices) {
            detectTapGestures { tap ->
                val c = Offset(size.width / 2f, size.height / 2f)
                val maxR = minOf(size.width, size.height) / 2f - 16f
                val hit = devices.filter { it.rssi != null }.minByOrNull { (pos(it, c, maxR) - tap).getDistance() }
                onSelect(if (hit != null && (pos(hit, c, maxR) - tap).getDistance() < 60f) hit.address else null)
            }
        },
    ) {
        val c = Offset(size.width / 2f, size.height / 2f)
        val maxR = minOf(size.width, size.height) / 2f - 16f
        for (f in listOf(0.25f, 0.5f, 0.75f, 1f)) drawCircle(FleetColors.Line, maxR * f, c, style = Stroke(width = 1.5f))
        drawLine(FleetColors.Line, Offset(c.x - maxR, c.y), Offset(c.x + maxR, c.y), 1f)
        drawLine(FleetColors.Line, Offset(c.x, c.y - maxR), Offset(c.x, c.y + maxR), 1f)
        drawCircle(FleetColors.Accent, 8f, c)
        for (d in devices) {
            if (d.rssi == null) continue
            val p = pos(d, c, maxR)
            val col = when {
                d.presence != DevicePresence.Detected -> FleetColors.Muted
                d.unstable -> FleetColors.Warn
                d.rssi < -78 -> FleetColors.Bad
                else -> FleetColors.Good
            }
            drawCircle(col, if (d.address == selected) 14f else 9f, p)
            if (d.address == selected) drawCircle(Color.White, 18f, p, style = Stroke(width = 2f))
        }
    }
}

private fun pos(d: DeviceRow, c: Offset, maxR: Float): Offset {
    val rssi = (d.rssi ?: -100).coerceIn(-100, -40)
    val frac = 0.08f + 0.92f * ((-40 - rssi) / 60f)           // stronger signal = nearer the centre
    val angle = Math.toRadians(((d.address.hashCode() and 0x7fffffff) % 360).toDouble())
    return Offset(c.x + (maxR * frac * cos(angle)).toFloat(), c.y + (maxR * frac * sin(angle)).toFloat())
}

/** Buckets raw observation windows into at most ~[maxPoints] chart points. */
fun bucketObservations(rows: List<ObservationEntity>, fromMs: Long, toMs: Long, maxPoints: Int = 300): Pair<List<ChartPoint>, Long> {
    val bucketMs = ((toMs - fromMs) / maxPoints).coerceAtLeast(1000)
    val points = rows.groupBy { (it.timestampMs - fromMs) / bucketMs }.toSortedMap().map { (b, list) ->
        ChartPoint(fromMs + b * bucketMs + bucketMs / 2, list.map { it.rssiAvg }.average(), list.minOf { it.rssiMin }, list.maxOf { it.rssiMax })
    }
    return points to bucketMs
}
