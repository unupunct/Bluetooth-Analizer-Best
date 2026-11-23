package com.unupunct.fleetmonitor.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Info
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.rotate
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.unupunct.fleetmonitor.bluetooth.GattStatus
import com.unupunct.fleetmonitor.bluetooth.MotionState
import com.unupunct.fleetmonitor.bluetooth.Permissions
import com.unupunct.fleetmonitor.domain.Compass
import com.unupunct.fleetmonitor.domain.DevicePresence
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.draw.rotate as rotateModifier
import kotlinx.coroutines.delay
import kotlin.math.abs
import kotlin.math.roundToInt
import com.unupunct.fleetmonitor.domain.LocateConfidence
import com.unupunct.fleetmonitor.domain.LocationEstimate
import com.unupunct.fleetmonitor.domain.Ranging
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin

/** One dot on the radar. [bearingDeg] is clockwise from north, or null when the direction is genuinely unknown. */
private class Blip(
    val address: String, val name: String, val radiusM: Double, val bearingDeg: Double?, val color: Color,
    val uncertaintyM: Double?, val alternative: Pair<Double, Double>? = null,
)

private val RANGES = listOf(5.0, 10.0, 25.0, 50.0, 100.0)

private fun pseudoBearing(address: String) = ((address.hashCode() and 0x7fffffff) % 360).toDouble()

/** Where a blip is drawn. Heading-up rotates blips with a known bearing so "up" is the way the user faces. */
private fun place(b: Blip, c: Offset, maxR: Float, rangeM: Double, headingDeg: Double?): Offset {
    val r = (min(b.radiusM, rangeM) / rangeM).toFloat() * maxR
    val a = Math.toRadians((b.bearingDeg?.let { it - (headingDeg ?: 0.0) } ?: pseudoBearing(b.address)))
    return Offset(c.x + r * sin(a).toFloat(), c.y - r * cos(a).toFloat())
}

@Composable
fun RadarScreen(vm: FleetViewModel, onOpen: (String) -> Unit) {
    val rows by vm.rows.collectAsState()
    val selected by vm.selected.collectAsState()
    val locating by vm.locating.collectAsState()
    val estimate by vm.locateEstimate.collectAsState()
    val motion by vm.motion.state.collectAsState()
    val settings by vm.settings.collectAsState()
    val target by vm.target.collectAsState()
    val watch by vm.watch.collectAsState()
    var query by remember { mutableStateOf("") }
    val ctx = LocalContext.current
    var rangeChoice by remember { mutableStateOf(0) }       // 0 = auto
    var headingUp by remember { mutableStateOf(true) }

    // The compass and step sensors only run while the radar is on screen (or a locate walk is in progress).
    DisposableEffect(Unit) {
        vm.motion.start(settings.strideCm / 100.0)
        onDispose { if (vm.locating.value == null) vm.motion.stop() }
    }

    val heard = rows.filter { it.presence == DevicePresence.Detected }
    // "Watch for it": the first device heard whose name, maker or type matches becomes the target, with a buzz.
    LaunchedEffect(rows, watch, target) {
        if (watch.isNotEmpty() && target == null) {
            rows.firstOrNull { it.presence == DevicePresence.Detected && it.matches(watch) }?.let { vm.setTarget(it.address); Haptics.pulse(ctx, 300) }
        }
    }
    val withRange = heard.filter { it.range != null }
    val useHeading = headingUp && motion.hasHeading
    val headingDeg = if (useHeading) motion.headingDeg else null

    val blips = withRange.take(150).map { r ->
        val isSel = r.address == selected
        val est = if (isSel && locating == r.address) estimate else null
        var radius = r.range!!.meters
        var bearing: Double? = null
        var unc: Double? = null
        var alt: Pair<Double, Double>? = null
        if (est != null) {
            val dx = est.x - motion.x; val dy = est.y - motion.y
            bearing = (Math.toDegrees(atan2(dx, dy)) + 360) % 360
            radius = hypot(dx, dy); unc = est.radiusMeters
            if (est.alternativeX != null && est.alternativeY != null) alt = (est.alternativeX - motion.x) to (est.alternativeY - motion.y)
        }
        val col = when { (r.rssi ?: -127) < -78 -> FleetColors.Bad; r.unstable -> FleetColors.Warn; (r.rssi ?: -127) >= -67 -> FleetColors.Good; else -> FleetColors.Accent }
        Blip(r.address, r.name, radius, bearing, col, unc, alt)
    }

    val autoRange = run {
        val radii = blips.map { it.radiusM }.sorted()
        val p80 = if (radii.isEmpty()) 5.0 else radii[(radii.size * 0.8).toInt().coerceAtMost(radii.size - 1)]
        val selR = blips.firstOrNull { it.address == selected }?.radiusM ?: 0.0
        RANGES.firstOrNull { it >= max(p80 * 1.1, selR * 1.3) } ?: RANGES.last()
    }
    val rangeM = if (rangeChoice == 0) autoRange else RANGES[rangeChoice - 1]
    val selectedRow = rows.firstOrNull { it.address == selected }

    val listState = rememberLazyListState()
    LaunchedEffect(selected) { if (selected != null) listState.animateScrollToItem(0) }
    var showInfo by remember { mutableStateOf(false) }
    var showAll by remember { mutableStateOf(false) }
    // Stable order: distance in ~30% buckets, then by address, so rows only move when a device is clearly nearer or farther.
    val sorted = heard.sortedWith(compareBy<DeviceRow> { it.range?.let { r -> Math.floor(Math.log(r.meters) / Math.log(1.3)).toInt() } ?: Int.MAX_VALUE }.thenBy { it.address })
    val others = sorted.filter { it.address != selected && it.matches(query) }
    val shown = if (showAll) others else others.take(25)

    LazyColumn(state = listState, modifier = Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item(key = "radar") {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Radar", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    listOf("Auto", "5 m", "10 m", "25 m", "50 m", "100 m").forEachIndexed { i, l -> FilterChip(rangeChoice == i, { rangeChoice = i }, label = { Text(l, fontSize = 12.sp) }) }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    FilterChip(headingUp, { headingUp = !headingUp }, enabled = motion.hasHeading, label = { Text(if (headingUp) "Heading up" else "North up", fontSize = 12.sp) })
                    Text("range ${Ranging.format(rangeM)}  |  ${blips.size} on radar" + (if (motion.hasHeading) "  |  ${motion.headingDeg.roundToInt()}° ${Compass.label(motion.headingDeg)}" else ""), color = FleetColors.Muted, fontSize = 12.sp, maxLines = 1, modifier = Modifier.weight(1f))
                }
                Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
                    RadarCanvas(blips, selected, target, rangeM, headingDeg, onSelect = { vm.select(it) }, modifier = Modifier.fillMaxSize())
                    IconButton(onClick = { showInfo = true }, modifier = Modifier.align(Alignment.TopEnd)) {
                        Icon(Icons.Default.Info, contentDescription = "About the radar", tint = FleetColors.Muted)
                    }
                }
            }
        }
        item(key = "search") {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth(), singleLine = true, placeholder = { Text("Search: name, maker, type or address") })
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (watch.isEmpty()) OutlinedButton(onClick = { vm.setWatch(query) }, enabled = query.isNotBlank()) { Text("Watch for it", fontSize = 12.sp) }
                    else {
                        Text("Watching for \"$watch\": it is selected and the phone buzzes when it is heard", color = FleetColors.Warn, fontSize = 12.sp, modifier = Modifier.weight(1f))
                        TextButton(onClick = { vm.setWatch("") }) { Text("Stop watching", fontSize = 12.sp) }
                    }
                }
            }
        }
        if (target != null) item(key = "target") {
            val tRow = rows.firstOrNull { it.address == target }
            Card2 {
                Text("Searching for ${tRow?.name ?: "this device"}", fontWeight = FontWeight.SemiBold)
                Text(
                    if (tRow != null && tRow.presence == DevicePresence.Detected) "Heard now" + (tRow.range?.let { ", about ${Ranging.format(it.meters)} away" } ?: "")
                    else "Not heard in the last ${settings.presenceWindowSec} s. Keep the scan running and move around; it will show on the radar when it is heard.",
                    color = FleetColors.Muted, fontSize = 12.sp,
                )
                TextButton(onClick = { vm.setTarget(null) }) { Text("Stop searching", fontSize = 12.sp) }
            }
        }
        // The selected device opens as the first entry of the list, directly under the radar.
        if (selectedRow != null) item(key = "selected") { DevicePanel(vm, selectedRow, locating == selectedRow.address, estimate, motion, onOpen) }
        item(key = "count") {
            Column {
                val matching = others.size + (if (selectedRow != null && selectedRow.matches(query)) 1 else 0)
                Text(if (query.isBlank()) "${heard.size} devices heard recently" else "$matching of ${heard.size} devices match \"$query\"", fontWeight = FontWeight.SemiBold)
                if (heard.isEmpty()) Text("Nothing heard yet. Start a scan on the Scan tab; devices appear here and on the radar within seconds.", color = FleetColors.Muted, fontSize = 13.sp)
            }
        }
        items(shown, key = { it.address }) { r -> RadarRow(r) { vm.select(r.address) } }
        if (others.size > 25) item(key = "more") { TextButton(onClick = { showAll = !showAll }) { Text(if (showAll) "Show fewer" else "Show all ${others.size}") } }
    }

    if (showInfo) AlertDialog(
        onDismissRequest = { showInfo = false },
        title = { Text("About the radar") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Distances are estimates from signal strength and are off by metres: walls, bodies and antenna angle all change it. Each estimate shows a likely range, for example \"about 3 m (probably 2 to 5 m)\". Calibrating a device at a known distance makes it much better.", fontSize = 13.sp)
                Text("A phone has one Bluetooth antenna, so it cannot sense direction. A hollow dot has an unknown direction: only its distance from the centre means anything. A filled dot has a direction worked out by walking (select a device and press Locate, then walk).", fontSize = 13.sp)
                Text("The compass ring shows N, E, S and W and turns as you turn (Heading up), so the top of the radar is the way you face. North up keeps north at the top instead. Colours: green strong signal, blue medium, red weak, amber unstable.", fontSize = 13.sp)
                Text("To find a device: search for it by name, maker or type, tap it, and press Follow the signal. Walk towards where it gets warmer; after a few steps and a turn an arrow shows the way, and the phone can vibrate faster as you get closer. \"Watch for it\" selects a device and buzzes the moment it is heard.", fontSize = 13.sp)
                Text("Tap a dot or a list entry to identify a device. From there you can calibrate it, locate it by walking, or run Connect and test (only on a device you pick, after a warning).", fontSize = 13.sp)
                Text("Devices appear as soon as they are heard; their distance firms up after a few seconds of signal.", color = FleetColors.Muted, fontSize = 12.sp)
            }
        },
        confirmButton = { TextButton(onClick = { showInfo = false }) { Text("OK") } },
    )
}

/** One device in the list under the radar. Tapping it selects it on the radar and opens its details at the top of the list. */
@Composable
private fun RadarRow(r: DeviceRow, onSelect: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().background(FleetColors.Panel, androidx.compose.foundation.shape.RoundedCornerShape(8.dp)).clickable { onSelect() }.padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(r.name, fontWeight = FontWeight.Medium, maxLines = 1, fontSize = 14.sp)
            Text(listOfNotNull(r.classification.category.takeIf { r.classification.isKnown }, r.classification.vendor ?: r.manufacturer).joinToString("  -  ").ifEmpty { "unidentified" },
                color = FleetColors.Muted, fontSize = 11.sp, maxLines = 1)
        }
        Column(horizontalAlignment = Alignment.End) {
            Text(r.range?.let { (if (it.provisional) "~" else "about ") + Ranging.format(it.meters) } ?: "-", fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
            Text("${r.rssi ?: "-"} dBm" + when (r.trend) { "Improving" -> "  rising"; "Degrading" -> "  falling"; else -> "" }, color = FleetColors.Muted, fontSize = 11.sp)
        }
    }
}

@Composable
private fun RadarCanvas(blips: List<Blip>, selected: String?, targetAddress: String?, rangeM: Double, headingDeg: Double?, onSelect: (String?) -> Unit, modifier: Modifier = Modifier) {
    val measurer = rememberTextMeasurer()
    val inf = rememberInfiniteTransition(label = "radar")
    val sweep by inf.animateFloat(0f, 360f, infiniteRepeatable(tween(5000, easing = LinearEasing)), label = "sweep")
    val pulse by inf.animateFloat(0f, 1f, infiniteRepeatable(tween(1500, easing = FastOutSlowInEasing), RepeatMode.Restart), label = "pulse")
    val nearest = blips.sortedBy { it.radiusM }.take(5).map { it.address }.toSet()

    Canvas(
        modifier.pointerInput(blips, rangeM, headingDeg) {
            detectTapGestures { tap ->
                val c = Offset(size.width / 2f, size.height / 2f); val maxR = min(size.width, size.height) / 2f - 38f
                val hit = blips.minByOrNull { (place(it, c, maxR, rangeM, headingDeg) - tap).getDistance() }
                onSelect(if (hit != null && (place(hit, c, maxR, rangeM, headingDeg) - tap).getDistance() < 64f) hit.address else null)
            }
        },
    ) {
        val c = Offset(size.width / 2f, size.height / 2f)
        val maxR = min(size.width, size.height) / 2f - 38f
        val grid = Color(0xFF1F4A3F)
        val label = TextStyle(color = Color(0xFF6FA89A), fontSize = 10.sp)

        drawCircle(Brush.radialGradient(listOf(Color(0xFF0E2A24), Color(0xFF08100F)), center = c, radius = maxR), maxR, c)
        // sweep
        rotate(sweep - 90f, c) {
            drawCircle(Brush.sweepGradient(colorStops = arrayOf(0f to Color.Transparent, 0.78f to Color.Transparent, 1f to Color(0x553DDC84)), center = c), maxR, c)
        }
        // range rings with metre labels
        for (k in 1..4) {
            drawCircle(grid, maxR * k / 4f, c, style = Stroke(width = if (k == 4) 2.5f else 1.2f))
            val m = rangeM * k / 4
            val t = Ranging.format(m)
            val tl = measurer.measure(t, label)
            drawText(tl, topLeft = Offset(c.x + maxR * k / 4f * 0.707f + 4f, c.y - maxR * k / 4f * 0.707f - tl.size.height))
        }
        drawLine(grid, Offset(c.x - maxR, c.y), Offset(c.x + maxR, c.y), 1f); drawLine(grid, Offset(c.x, c.y - maxR), Offset(c.x, c.y + maxR), 1f)
        // Compass ring: N / E / S / W and a tick every 30 degrees. It turns as the phone turns (heading up) so you can point the phone at a dot.
        val ringRot = -(headingDeg ?: 0.0)
        for (deg in 0 until 360 step 30) {
            val a = Math.toRadians(deg + ringRot)
            val len = if (deg % 90 == 0) 14f else 7f
            drawLine(FleetColors.Muted.copy(alpha = 0.7f), Offset(c.x + maxR * sin(a).toFloat(), c.y - maxR * cos(a).toFloat()), Offset(c.x + (maxR + len) * sin(a).toFloat(), c.y - (maxR + len) * cos(a).toFloat()), if (deg % 90 == 0) 3f else 1.5f)
        }
        for ((deg, letter) in listOf(0 to "N", 90 to "E", 180 to "S", 270 to "W")) {
            val a = Math.toRadians(deg + ringRot)
            val lt = measurer.measure(letter, TextStyle(color = if (deg == 0) FleetColors.Bad else FleetColors.Fg, fontSize = 14.sp, fontWeight = FontWeight.Bold))
            val p = Offset(c.x + (maxR + 26f) * sin(a).toFloat(), c.y - (maxR + 26f) * cos(a).toFloat())
            drawText(lt, topLeft = Offset(p.x - lt.size.width / 2f, p.y - lt.size.height / 2f))
        }
        // the way you are facing: a fixed marker at the top in heading-up, a turning one in north-up
        val faceDeg = if (headingDeg != null) 0.0 else null
        if (faceDeg != null) {
            val tri = Path().apply { moveTo(c.x, c.y - maxR - 2f); lineTo(c.x - 9f, c.y - maxR - 18f); lineTo(c.x + 9f, c.y - maxR - 18f); close() }
            drawPath(tri, FleetColors.Accent)
        }
        drawCircle(FleetColors.Accent, 7f, c); drawCircle(FleetColors.Accent.copy(alpha = 0.25f), 16f, c)

        // devices: farthest first so near ones are on top
        for (b in blips.sortedByDescending { it.radiusM }) {
            val p = place(b, c, maxR, rangeM, headingDeg)
            val known = b.bearingDeg != null
            val isSel = b.address == selected
            if (isSel) {
                // error circle (known position) and a pulsing ring
                b.uncertaintyM?.let { u -> drawCircle(b.color.copy(alpha = 0.12f), (min(u, rangeM) / rangeM).toFloat() * maxR, p); drawCircle(b.color.copy(alpha = 0.5f), (min(u, rangeM) / rangeM).toFloat() * maxR, p, style = Stroke(1.5f)) }
                drawCircle(Color.White.copy(alpha = 1f - pulse), 14f + 26f * pulse, p, style = Stroke(2.5f))
                drawLine(b.color.copy(alpha = 0.5f), c, p, 2f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(10f, 10f)))
            }
            val dim = targetAddress != null && b.address != targetAddress           // searching: everything else fades
            val col = if (dim) b.color.copy(alpha = 0.22f) else b.color
            val r = if (isSel || b.address == targetAddress) 11f else 8f
            drawCircle(col.copy(alpha = if (dim) 0.05f else 0.18f), r * 2.4f, p)               // glow
            if (known) drawCircle(col, r, p) else { drawCircle(col.copy(alpha = 0.15f), r, p); drawCircle(col, r, p, style = Stroke(2.5f)) }
            if (isSel || b.address == targetAddress || (!dim && b.address in nearest)) {
                val txt = measurer.measure("${b.name.take(16)} ${Ranging.format(b.radiusM)}", TextStyle(color = if (isSel) Color.White else Color(0xFFBFD8D1), fontSize = 10.sp, fontWeight = if (isSel) FontWeight.Bold else FontWeight.Normal))
                val tx = (p.x + r + 6f).coerceAtMost(size.width - txt.size.width - 2f)
                drawText(txt, topLeft = Offset(tx, p.y - txt.size.height / 2f))
            }
            b.alternative?.let { (ax, ay) ->            // the mirror-image alternative, dashed
                val bearing = (Math.toDegrees(atan2(ax, ay)) + 360) % 360
                val ab = Blip(b.address, b.name, hypot(ax, ay), bearing, b.color, null)
                val ap = place(ab, c, maxR, rangeM, headingDeg)
                drawCircle(b.color.copy(alpha = 0.7f), 9f, ap, style = Stroke(2f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(6f, 6f))))
            }
        }
    }
}

// ============================================================ selected-device panel

@Composable
fun DevicePanel(vm: FleetViewModel, row: DeviceRow, locating: Boolean, estimate: LocationEstimate?, motion: MotionState, onOpen: (String) -> Unit) {
    val ctx = LocalContext.current
    val cls = row.classification
    val range = row.range
    var showEvidence by remember(row.address) { mutableStateOf(false) }
    var showCalibrate by remember(row.address) { mutableStateOf(false) }
    var showConnect by remember(row.address) { mutableStateOf(false) }
    var calMessage by remember(row.address) { mutableStateOf<String?>(null) }
    val gattUi by vm.gattUi.collectAsState()
    val tests by vm.gattHistory(row.address).collectAsState(initial = emptyList())
    val walkSamples by vm.walkSamples.collectAsState()
    val connectLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { if (Permissions.hasConnectPermission(ctx)) vm.runConnectTest(row.address) }
    val targetNow by vm.target.collectAsState()
    var vibrate by remember { mutableStateOf(true) }
    val distNow = estimate?.let { hypot(it.x - motion.x, it.y - motion.y) } ?: range?.meters ?: 10.0
    val distLatest by rememberUpdatedState(distNow)
    // Closer means faster pulses, so you can follow the signal without looking at the screen.
    LaunchedEffect(locating, vibrate) {
        while (locating && vibrate) { Haptics.pulse(ctx); delay(Haptics.intervalForDistance(distLatest)) }
    }

    Card2 {
        Text(row.name, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
        Text(row.address.chunked(2).joinToString(":"), color = FleetColors.Muted, fontSize = 11.sp)

        // ---- identity
        Spacer(Modifier.height(6.dp))
        Label("What it is")
        if (cls.isKnown) {
            Text("${cls.category}${cls.vendor?.let { "  -  $it" } ?: ""}", fontWeight = FontWeight.SemiBold)
            Text("confidence ${(cls.confidence * 100).toInt()}%  (from what it advertises, not verified)", color = FleetColors.Muted, fontSize = 12.sp)
        } else {
            Text(cls.vendor?.let { "Unknown type  -  maker: $it" } ?: "Unknown", fontWeight = FontWeight.SemiBold)
            Text("Its advertisement does not say what it is. \"Connect and test\" can read its own name and model.", color = FleetColors.Muted, fontSize = 12.sp)
        }
        if (row.gattName != null || row.gattModel != null || row.gattManufacturer != null) {
            Text("Read from the device: ${listOfNotNull(row.gattName, row.gattManufacturer, row.gattModel, row.gattFirmware?.let { "fw $it" }).joinToString(" | ")}", fontSize = 12.sp)
        }
        TextButton(onClick = { showEvidence = !showEvidence }) { Text(if (showEvidence) "Hide evidence" else "Show evidence (${cls.evidence.size})", fontSize = 12.sp) }
        if (showEvidence) cls.evidence.forEach { Text("- $it", color = FleetColors.Muted, fontSize = 12.sp) }

        // ---- signal and distance
        Spacer(Modifier.height(4.dp))
        Label("Signal and distance")
        val shownRssi = range?.smoothedRssi?.let { Math.round(it).toInt() } ?: row.rssi
        Row(verticalAlignment = Alignment.Bottom) {
            Text(shownRssi?.toString() ?: "-", fontSize = 34.sp, fontWeight = FontWeight.Bold)
            Text(" dBm ${if (range != null && !range.provisional) "(smoothed) " else ""} ${shownRssi?.let { com.unupunct.fleetmonitor.domain.Rssi.describe(it) } ?: ""}", color = FleetColors.Muted)
        }
        if (range != null && row.rssi != null && !range.provisional) Text("last single reading ${row.rssi} dBm; single readings jump by several dB, the smoothed value is what the distance uses", color = FleetColors.Muted, fontSize = 11.sp)
        Sparkline(row.samples, Modifier.fillMaxWidth().height(48.dp))
        if (range != null) {
            Text("About ${Ranging.format(range.meters)}  (probably ${Ranging.format(range.lowMeters)} to ${Ranging.format(range.highMeters)})", fontWeight = FontWeight.SemiBold)
            Text((if (range.provisional) "First reading only; a few seconds of signal will firm it up. " else "") + "Based on a model ${range.basis.label}. " + if (range.basis == com.unupunct.fleetmonitor.domain.RangeBasis.Default) "Not calibrated, so treat this as a rough guide." else "", color = FleetColors.Muted, fontSize = 12.sp)
            val s = row.samples
            if (s.size >= 8) {
                val d = s.takeLast(3).average() - s.subList(s.size - 8, s.size - 5).average()
                Text(if (d > 1.5) "Signal rising: getting closer" else if (d < -1.5) "Signal falling: moving away" else "Signal steady",
                    color = if (d > 1.5) FleetColors.Good else if (d < -1.5) FleetColors.Warn else FleetColors.Muted, fontWeight = FontWeight.SemiBold)
            }
        } else Text("Collecting signal... (needs a few seconds)", color = FleetColors.Muted, fontSize = 13.sp)
        row.calibRssi1m?.let { Text("Calibrated: $it dBm at 1 m", color = FleetColors.Good, fontSize = 12.sp) }
        calMessage?.let { Text(it, color = FleetColors.Warn, fontSize = 12.sp) }

        // ---- follow the signal
        Spacer(Modifier.height(6.dp))
        if (locating) {
            Label("Following the signal")
            val smoothed = range?.smoothedRssi
            val fraction = smoothed?.let { ((it + 95.0) / 55.0).coerceIn(0.0, 1.0).toFloat() } ?: 0f
            val s = row.samples
            val d = if (s.size >= 8) s.takeLast(3).average() - s.subList(s.size - 8, s.size - 5).average() else 0.0
            val warm = if (distNow < 1.2) "Very close" else if (d > 1.5) "Warmer" else if (d < -1.5) "Colder" else "Steady"
            val warmColor = if (distNow < 1.2 || d > 1.5) FleetColors.Good else if (d < -1.5) FleetColors.Warn else FleetColors.Muted
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(warm, fontSize = 26.sp, fontWeight = FontWeight.Bold, color = warmColor)
                Spacer(Modifier.width(14.dp))
                Column {
                    Text("about ${Ranging.format(distNow)}", fontWeight = FontWeight.SemiBold)
                    Text("${smoothed?.roundToInt() ?: "-"} dBm smoothed", color = FleetColors.Muted, fontSize = 12.sp)
                }
            }
            LinearProgressIndicator(progress = { fraction }, modifier = Modifier.fillMaxWidth().height(10.dp), color = warmColor, trackColor = FleetColors.Line)
            Text("Steps ${motion.steps}  |  readings $walkSamples", color = FleetColors.Muted, fontSize = 12.sp)
            val e = estimate
            when {
                !motion.hasHeading -> Text("No compass reading yet. Hold the phone upright, facing the way you walk.", color = FleetColors.Warn, fontSize = 13.sp)
                e == null -> Text("Walk towards where the signal gets stronger. After a few steps (and a turn) an arrow appears that points at the device.", fontSize = 13.sp)
                else -> {
                    val dx = e.x - motion.x; val dy = e.y - motion.y
                    val bearing = (Math.toDegrees(atan2(dx, dy)) + 360) % 360
                    val turn = Compass.turnTo(bearing, motion.headingDeg)
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Canvas(Modifier.size(76.dp)) {
                            val c = Offset(size.width / 2f, size.height / 2f)
                            drawCircle(FleetColors.Line, size.minDimension / 2f - 2f, c, style = Stroke(2f))
                            val arrow = Path().apply { moveTo(c.x, 8f); lineTo(c.x + 22f, c.y + 20f); lineTo(c.x, c.y + 8f); lineTo(c.x - 22f, c.y + 20f); close() }
                            rotate(turn.toFloat(), c) { drawPath(arrow, if (abs(turn) < 20) FleetColors.Good else FleetColors.Accent) }
                        }
                        Spacer(Modifier.width(12.dp))
                        Column {
                            Text(if (abs(turn) < 15) "Straight ahead" else if (abs(turn) > 150) "Turn around" else "Turn ${if (turn < 0) "left" else "right"} ${abs(turn).roundToInt()} deg", fontWeight = FontWeight.Bold, fontSize = 18.sp)
                            Text("Bearing ${bearing.roundToInt()} deg (${Compass.label(bearing)}), error about ${Ranging.format(e.radiusMeters)}, confidence ${e.confidence.label}", color = FleetColors.Muted, fontSize = 12.sp)
                        }
                    }
                    if (e.mirrorAmbiguous) Text("Two positions fit equally (the dashed circle is the other one). Turn and walk about 5 m in a different direction to settle it.", color = FleetColors.Warn, fontSize = 12.sp)
                    else if (e.confidence == LocateConfidence.Low) Text("Walk further, with a turn, for a firmer answer.", color = FleetColors.Muted, fontSize = 12.sp)
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Switch(vibrate, { vibrate = it }); Text("  Vibrate faster as I get closer", fontSize = 13.sp)
            }
            OutlinedButton(onClick = { vm.stopLocate() }) { Text("Stop following") }
        } else {
            Button(onClick = { vm.startLocate(row.address) }, enabled = range != null && vm.motion.available) { Text("Follow the signal: walk towards it") }
            if (!vm.motion.available) Text("This phone lacks a compass or accelerometer, so walking cannot be tracked.", color = FleetColors.Warn, fontSize = 12.sp)
        }
        Spacer(Modifier.height(6.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { showCalibrate = true }, enabled = range != null) { Text("Calibrate", fontSize = 13.sp) }
            OutlinedButton(onClick = { showConnect = true }, enabled = row.connectable != false) { Text("Connect and test", fontSize = 13.sp) }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { vm.setTarget(if (targetNow == row.address) null else row.address) }) { Text(if (targetNow == row.address) "Stop searching for it" else "Search for this device", fontSize = 13.sp) }
            OutlinedButton(onClick = { onOpen(row.address) }) { Text("Details", fontSize = 13.sp) }
        }
        if (row.connectable == false) Text("This device is not accepting connections right now (it is probably already connected to another app), so it cannot be tested without disturbing it.", color = FleetColors.Muted, fontSize = 11.sp)

        // ---- connection test result
        when (val g = gattUi) {
            is GattUi.Running -> if (g.address == row.address) { Spacer(Modifier.height(6.dp)); Text("Connecting and reading... (up to about 25 s)", color = FleetColors.Warn) }
            is GattUi.Done -> if (g.address == row.address) { Spacer(Modifier.height(6.dp)); GattResultCard(g.result) { vm.dismissGatt() } }
            else -> {}
        }
        if (tests.isNotEmpty()) {
            Spacer(Modifier.height(4.dp)); Label("Earlier connection tests")
            tests.take(3).forEach { t -> Text("${fmtTime(t.startedMs, "dd MMM HH:mm")}  ${t.outcome}  ${t.durationMs / 1000.0}s" + (t.rssiAvg?.let { "  link RSSI avg ${"%.0f".format(it)} dBm" } ?: ""), color = FleetColors.Muted, fontSize = 11.sp) }
        }
    }

    if (showCalibrate) CalibrateDialog(
        onDismiss = { showCalibrate = false },
        onCalibrate = { meters ->
            val ok = vm.calibrate(row.address, meters)
            calMessage = if (ok) "Calibrated at ${Ranging.format(meters)}." else "Not enough signal data yet; wait a few seconds and try again."
            showCalibrate = false
        },
        onClear = { vm.clearCalibration(row.address); calMessage = "Calibration cleared."; showCalibrate = false },
        hasCalibration = row.calibRssi1m != null,
    )
    if (showConnect) ConnectDialog(
        row = row,
        onDismiss = { showConnect = false },
        onConnect = {
            showConnect = false
            if (Permissions.hasConnectPermission(ctx)) vm.runConnectTest(row.address) else connectLauncher.launch(Permissions.connectPermissions())
        },
    )
}

private fun compass(deg: Double): String = listOf("N", "NE", "E", "SE", "S", "SW", "W", "NW")[((deg + 22.5) / 45).toInt() % 8]

@Composable
private fun CalibrateDialog(onDismiss: () -> Unit, onCalibrate: (Double) -> Unit, onClear: () -> Unit, hasCalibration: Boolean) {
    var text by remember { mutableStateOf("1") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Calibrate this device") },
        text = {
            Column {
                Text("Stand a known distance from the device with a clear line between you, hold the phone still for about 10 seconds, then press Calibrate. The app learns this device's signal level, which makes its distance estimates far more accurate.", fontSize = 13.sp)
                OutlinedTextField(text, { text = it.filter { c -> c.isDigit() || c == '.' }.take(5) }, singleLine = true, label = { Text("Distance to the device (metres)") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.padding(top = 8.dp))
            }
        },
        confirmButton = { Button(onClick = { text.toDoubleOrNull()?.takeIf { it in 0.2..30.0 }?.let(onCalibrate) }) { Text("Calibrate now") } },
        dismissButton = { Row { if (hasCalibration) TextButton(onClick = onClear) { Text("Clear") }; TextButton(onClick = onDismiss) { Text("Cancel") } } },
    )
}

@Composable
private fun ConnectDialog(row: DeviceRow, onDismiss: () -> Unit, onConnect: () -> Unit) {
    var understood by remember { mutableStateOf(false) }
    val lovense = row.classification.isLovense
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Connect to ${row.name}?") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text("This opens a short Bluetooth connection to this one device, reads its standard name / maker / model and measures the link signal for about 5 seconds, then disconnects.", fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                Text("It never writes to the device, never pairs, and sends no commands.", fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(6.dp))
                Text("A connection can disturb something that is already using the device: some devices drop their current link, and a device already connected to another app usually refuses a second connection. Do not run this on a device that is in use right now.", color = FleetColors.Warn, fontSize = 13.sp)
                if (lovense) {
                    Spacer(Modifier.height(6.dp))
                    Text("This looks like a Lovense device. Do not connect while it is in a live session or being controlled by the Lovense app. This test sends no control commands, but a connection can still interrupt its current session.", color = FleetColors.Bad, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                }
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                    Checkbox(understood, { understood = it })
                    Text("I understand and nothing is using this device right now.", fontSize = 13.sp)
                }
            }
        },
        confirmButton = { Button(onClick = onConnect, enabled = understood) { Text("Connect and test") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun GattResultCard(r: com.unupunct.fleetmonitor.bluetooth.GattResult, onDismiss: () -> Unit) {
    Column(Modifier.fillMaxWidth()) {
        Label("Connection test")
        Text(if (r.success) "Connected: ${"%.1f".format(r.durationMs / 1000.0)} s in total" else "${r.outcome}: ${r.detail}", fontWeight = FontWeight.SemiBold,
            color = if (r.success) FleetColors.Good else FleetColors.Warn)
        if (!r.success && r.gattStatus != null) Text("Status: ${GattStatus.describe(r.gattStatus)}", color = FleetColors.Muted, fontSize = 12.sp)
        if (r.success) {
            listOf("Name" to r.name, "Maker" to r.manufacturer, "Model" to r.model, "Firmware" to r.firmware).forEach { (k, v) -> if (v != null) Text("$k: $v", fontSize = 13.sp) }
            r.appearance?.let { Text("Appearance code: 0x%04X".format(it), fontSize = 12.sp, color = FleetColors.Muted) }
            if (r.rssiReads.isNotEmpty()) Text("Link signal over ${r.rssiReads.size} readings: ${r.rssiReads.min()} to ${r.rssiReads.max()} dBm, average ${"%.0f".format(r.rssiReads.average())}", fontSize = 13.sp)
            Text("${r.services.size} services offered", color = FleetColors.Muted, fontSize = 12.sp)
            if (r.name == null && r.model == null && r.manufacturer == null) Text("The device connected but does not share its name or model (it keeps them protected).", color = FleetColors.Muted, fontSize = 12.sp)
        }
        TextButton(onClick = onDismiss) { Text("Dismiss", fontSize = 12.sp) }
    }
}

/** Does this device match what the user typed? Name, maker, type, address, or a name read from the device. */
private fun DeviceRow.matches(q: String): Boolean {
    val text = q.trim()
    if (text.isEmpty()) return true
    return name.contains(text, true) || address.contains(text.replace(":", ""), true) || (manufacturer?.contains(text, true) ?: false) ||
        classification.category.contains(text, true) || (classification.vendor?.contains(text, true) ?: false) || (gattName?.contains(text, true) ?: false)
}