package com.unupunct.fleetmonitor.ui

import android.content.Context
import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.text.KeyboardOptions
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.unupunct.fleetmonitor.bluetooth.Permissions
import com.unupunct.fleetmonitor.bluetooth.ScanService
import com.unupunct.fleetmonitor.data.ScanMode
import com.unupunct.fleetmonitor.domain.DevicePresence
import com.unupunct.fleetmonitor.domain.ScannerState
import com.unupunct.fleetmonitor.domain.Rssi

private class PhoneState(val permission: Boolean, val bluetooth: Boolean, val location: Boolean, val notifications: Boolean, val battery: Boolean, val connect: Boolean = true)

/** Shows the system "Allow app to always run in the background?" dialog; the user taps Allow. Falls back to the battery list on phones without it. */
private fun requestBattery(ctx: Context) {
    try { ctx.startActivity(Permissions.batteryExemptionIntent(ctx)) }
    catch (e: Exception) { runCatching { ctx.startActivity(Permissions.batteryListIntent()) } }
}

/** Re-reads permission, Bluetooth, Location and battery state every time the screen comes back to the front. */
@Composable
private fun rememberPhoneState(): State<PhoneState> {
    val ctx = LocalContext.current
    fun read() = PhoneState(Permissions.hasScanPermission(ctx), Permissions.bluetoothEnabled(ctx), Permissions.locationServicesOn(ctx),
        Permissions.hasNotificationPermission(ctx), Permissions.ignoringBatteryOptimisations(ctx), Permissions.hasConnectPermission(ctx))
    val state = remember { mutableStateOf(read()) }
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        val obs = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_RESUME) state.value = read() }
        owner.lifecycle.addObserver(obs)
        onDispose { owner.lifecycle.removeObserver(obs) }
    }
    return state
}

@Composable
private fun Screen(title: String, content: @Composable ColumnScope.() -> Unit) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(title, fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
        content()
    }
}

@Composable
private fun SetupRow(ok: Boolean, title: String, detail: String, action: String?, onAction: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(if (ok) "OK" else "!", color = if (ok) FleetColors.Good else FleetColors.Warn, fontWeight = FontWeight.Bold, modifier = Modifier.width(30.dp))
        Column(Modifier.weight(1f)) {
            Text(title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
            Text(detail, color = FleetColors.Muted, fontSize = 12.sp)
        }
        if (!ok && action != null) OutlinedButton(onClick = onAction) { Text(action, fontSize = 12.sp) }
    }
}

/** One place that says what the app needs, whether it has it, and lets the user grant it (the system always shows its own confirmation). */
@Composable
private fun SetupCard(phone: PhoneState, ctx: Context, onAllowEverything: () -> Unit) {
    val sdk = android.os.Build.VERSION.SDK_INT
    val allOk = phone.permission && phone.bluetooth && (sdk >= 31 || phone.location) && phone.notifications && phone.battery && phone.connect
    if (allOk) {
        Card2 { Text("OK  Setup complete: the app has the permissions it needs.", color = FleetColors.Good, fontSize = 13.sp) }
        return
    }
    Column(
        Modifier.fillMaxWidth().background(FleetColors.WarnBg, RoundedCornerShape(10.dp)).border(1.dp, FleetColors.Warn, RoundedCornerShape(10.dp)).padding(12.dp),
    ) {
        Text("Setup: allow what the app needs", color = FleetColors.Warn, fontWeight = FontWeight.SemiBold)
        Text("Android shows its own confirmation for each one. Nothing here is used to track you.", color = FleetColors.Fg, fontSize = 12.sp)
        Spacer(Modifier.height(6.dp))
        SetupRow(phone.permission, if (sdk >= 31) "Nearby devices" else "Location permission",
            if (sdk >= 31) "To hear Bluetooth advertisements. Never used to find your location." else "Android 10 and 11 require it to scan for Bluetooth. Your location is never recorded.", "Allow", onAllowEverything)
        SetupRow(phone.notifications, "Notifications", "Shows the \"scanning\" notice, with a Stop button, while a scan runs in the background.", "Allow", onAllowEverything)
        SetupRow(phone.connect, "Bluetooth connect", "Only for \"Connect and test\" on a device you pick, to read its name and model.", "Allow", onAllowEverything)
        SetupRow(phone.battery, "Run in the background", "Android otherwise pauses or kills long scans to save battery. Choose Allow in the next dialog.", "Allow") { requestBattery(ctx) }
        SetupRow(phone.bluetooth, "Bluetooth is on", "A running scan resumes by itself when Bluetooth comes back; the gap is recorded.", null) {}
        if (sdk < 31) SetupRow(phone.location, "Location setting is on", "Android 10 and 11 deliver no scan results while it is off. No location is recorded.", "Open") {
            runCatching { ctx.startActivity(Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS)) }
        }
        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = onAllowEverything) { Text("Allow everything") }
            OutlinedButton(onClick = { runCatching { ctx.startActivity(Permissions.appDetailsIntent(ctx)) } }) { Text("App settings") }
        }
        if (android.os.Build.MANUFACTURER.equals("samsung", true))
            Text("Samsung: also set Battery to Unrestricted in App settings, and keep the app out of \"Sleeping apps\".", color = FleetColors.Muted, fontSize = 11.sp, modifier = Modifier.padding(top = 6.dp))
    }
}

// ============================================================ 1. Scanner

@Composable
fun ScannerScreen(vm: FleetViewModel, onStarted: () -> Unit = {}) {
    val ctx = LocalContext.current
    val status by vm.status.collectAsState()
    val counters by vm.counters.collectAsState()
    val running by vm.mode.collectAsState()
    val rows by vm.rows.collectAsState()
    val gaps by vm.gaps.collectAsState()
    val scope by vm.engine.scanScope.collectAsState()
    val phone by rememberPhoneState()
    val settings by vm.settings.collectAsState()
    var selected by remember { mutableStateOf(vm.mode.value) }
    var error by remember { mutableStateOf<String?>(null) }
    var chainBattery by remember { mutableStateOf(false) }
    // "Allow everything": ask for the runtime permissions, then (once those are answered) the battery exemption.
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (chainBattery && !Permissions.ignoringBatteryOptimisations(ctx)) requestBattery(ctx)
        chainBattery = false
    }
    val scanning = status.state == ScannerState.Running || status.state == ScannerState.RunningNoAdvertisements

    Screen("Scanner") {
        SetupCard(phone, ctx) { chainBattery = true; launcher.launch(Permissions.everything()) }
        error?.let { Banner("Could not start", it) }
        if (status.state == ScannerState.RunningNoAdvertisements || status.state == ScannerState.Interrupted) Banner("Scanner needs attention", status.message)

        Card2 {
            Label("State")
            Text(status.state.name, fontSize = 22.sp, fontWeight = FontWeight.SemiBold,
                color = when (status.state) { ScannerState.Running -> FleetColors.Good; ScannerState.Stopped -> FleetColors.Muted; else -> FleetColors.Warn })
            Text(status.message, color = FleetColors.Muted, fontSize = 13.sp)
            Spacer(Modifier.height(8.dp))
            Label("Scope"); Text(scope, fontSize = 13.sp, color = if (scope == com.unupunct.fleetmonitor.bluetooth.ScanEngine.SCOPE_ALL) FleetColors.Fg else FleetColors.Warn)
            Spacer(Modifier.height(8.dp))
            Label("Last advertisement"); Text(fmtTime(status.lastAdvertisementMs))
            Spacer(Modifier.height(8.dp))
            Label("Counters")
            Text("${counters.heard} heard | ${counters.stored} windows stored | ${rows.size} devices | ${counters.dropped} dropped | ${counters.lost} lost to write errors", fontSize = 13.sp)
        }

        Label("Mode")
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            ScanMode.values().forEach { m ->
                FilterChip(selected = selected == m, onClick = { selected = m }, enabled = !scanning, label = { Text(m.label) })
            }
        }
        Text(
            when (selected) {
                ScanMode.Continuous -> "Foreground service with a visible notification. Scans every nearby device while the screen is on. With the screen off Android pauses open-ended scans, so the phone then watches only the devices already known. Stores one window per device every ${settings.sampleContinuousSec} s."
                ScanMode.Background -> "Android-managed filtered scan that can survive the app being closed. Set exact device names in Settings; unfiltered scans are paused when the screen is off. Android may still stop it."
                ScanMode.Inspection -> "For walking around: stores a window every ${settings.sampleInspectionSec} s. Start an inspection on the Sessions tab to tag results with a location."
            }, color = FleetColors.Muted, fontSize = 13.sp,
        )

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (!scanning) {
                Button(onClick = {
                    error = vm.engine.preflightError()
                    if (error != null) { if (!phone.permission) launcher.launch(Permissions.requestable()); return@Button }
                    if (!phone.notifications && selected != ScanMode.Background) launcher.launch(Permissions.requestable())
                    if (selected == ScanMode.Background) error = vm.engine.start(ScanMode.Background) else ScanService.start(ctx, selected)
                    if (error == null) onStarted()      // straight to the radar
                }) { Text("Start scanning") }
                if (!phone.permission) OutlinedButton(onClick = { launcher.launch(Permissions.requestable()) }) { Text("Grant permission") }
            } else {
                Button(onClick = { if (running == ScanMode.Background) vm.engine.stop(true) else ScanService.stop(ctx) },
                    colors = ButtonDefaults.buttonColors(containerColor = FleetColors.Bad)) { Text("Stop scanning") }
            }
            if (!phone.battery) OutlinedButton(onClick = { requestBattery(ctx) }) { Text("Allow background use") }
        }

        if (gaps.isNotEmpty()) {
            Label("Coverage gaps (periods the phone could not hear)")
            gaps.take(5).forEach { g ->
                Text("${fmtTime(g.startMs, "dd MMM HH:mm:ss")} to ${g.endMs?.let { fmtTime(it) } ?: "ongoing"} - ${g.reason}", fontSize = 12.sp, color = FleetColors.Muted)
            }
            Text("Devices are never blamed for silence inside a gap.", fontSize = 11.sp, color = FleetColors.Muted)
        }
    }
}

// ============================================================ 2. Nearby devices

@Composable
fun NearbyScreen(vm: FleetViewModel, onOpen: (String) -> Unit) {
    val rows by vm.rows.collectAsState()
    var search by remember { mutableStateOf("") }
    var filter by remember { mutableStateOf("All") }
    var sort by remember { mutableStateOf("Last heard") }
    val filtered = rows.filter { r ->
        (search.isBlank() || r.name.contains(search, true) || r.address.contains(search, true) || (r.manufacturer?.contains(search, true) ?: false)) &&
            when (filter) {
                "Heard now" -> r.presence == DevicePresence.Detected
                "Not heard" -> r.presence != DevicePresence.Detected
                "Weak" -> (r.rssi ?: -127) < -78
                "Unstable" -> r.unstable
                else -> true
            }
    }.let { list ->
        when (sort) {
            "Signal" -> list.sortedByDescending { it.rssi ?: -127 }
            "Name" -> list.sortedBy { it.name.lowercase() }
            else -> list.sortedByDescending { it.lastSeenMs ?: 0 }
        }
    }
    Column(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
        Spacer(Modifier.height(12.dp))
        Text("Nearby devices", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
        OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth().padding(vertical = 8.dp), singleLine = true, placeholder = { Text("Search name, address, maker") })
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.chipRow()) {
            listOf("All", "Heard now", "Not heard", "Weak", "Unstable").forEach { f -> FilterChip(filter == f, { filter = f }, label = { Text(f) }) }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp).chipRow()) {
            Text("Sort", color = FleetColors.Muted, fontSize = 12.sp)
            listOf("Last heard", "Signal", "Name").forEach { s -> FilterChip(sort == s, { sort = s }, label = { Text(s) }) }
        }
        Text("${filtered.size} of ${rows.size} devices", color = FleetColors.Muted, fontSize = 12.sp, modifier = Modifier.padding(vertical = 6.dp))
        if (rows.isEmpty()) Text("Nothing heard yet. Start a scan on the Scanner tab.", color = FleetColors.Muted)
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(filtered, key = { it.address }) { r -> DeviceCard(r) { onOpen(r.address) } }
        }
    }
}

@Composable
private fun Modifier.horizontalScrollCompat(): Modifier = this.then(Modifier.fillMaxWidth())

@Composable
private fun Modifier.chipRow(): Modifier = this.fillMaxWidth().horizontalScroll(rememberScrollState())

@Composable
fun DeviceCard(r: DeviceRow, onClick: () -> Unit) {
    Card2(Modifier.clickable { onClick() }) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(r.name, fontWeight = FontWeight.SemiBold, maxLines = 1)
                Text("${r.address.takeLast(8).chunked(2).joinToString(":")}  ${r.manufacturer ?: ""}", color = FleetColors.Muted, fontSize = 12.sp)
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(r.rssi?.let { "$it dBm" } ?: "-", fontWeight = FontWeight.SemiBold)
                Text(r.quality, color = FleetColors.Muted, fontSize = 12.sp)
            }
        }
        Spacer(Modifier.height(4.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(presenceText(r.presence), color = presenceColor(r.presence), fontSize = 12.sp)
            Text(ago(r.lastSeenMs), color = FleetColors.Muted, fontSize = 12.sp)
            if (r.trend != "-") Text(r.trend, color = if (r.trend == "Degrading") FleetColors.Warn else FleetColors.Muted, fontSize = 12.sp)
            if (r.unstable) Text("Unstable", color = FleetColors.Warn, fontSize = 12.sp)
        }
    }
}

// ============================================================ 4. Device inspector

@Composable
fun InspectorScreen(vm: FleetViewModel, address: String, onHistory: (String) -> Unit, onBack: () -> Unit, onRadar: (String) -> Unit = {}) {
    val rows by vm.rows.collectAsState()
    val r = rows.firstOrNull { it.address == address }
    var rename by remember(address) { mutableStateOf("") }
    val stats by produceState<Triple<Int, Double?, Double?>?>(null, address, r?.lastSeenMs) {
        val now = System.currentTimeMillis()
        val recent = vm.history(address, now - 5 * 60_000)
        val previous = vm.previousAverage(address, now - 5 * 60_000)
        value = Triple(recent.size, if (recent.isEmpty()) null else recent.map { it.rssiAvg }.average(), previous)
    }
    Screen("Device inspector") {
        TextButton(onClick = onBack) { Text("< Back") }
        if (r == null) { Text("This device is not known.", color = FleetColors.Muted); return@Screen }
        Card2 {
            Text(r.name, fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            Text(r.address.chunked(2).joinToString(":"), color = FleetColors.Muted, fontSize = 12.sp)
            Text(r.manufacturer ?: "Unknown maker", color = FleetColors.Muted, fontSize = 12.sp)
            Spacer(Modifier.height(8.dp))
            Row(verticalAlignment = Alignment.Bottom) {
                Text(r.rssi?.let { "$it" } ?: "-", fontSize = 44.sp, fontWeight = FontWeight.Bold); Text(" dBm  ${r.quality}", color = FleetColors.Muted)
            }
            Sparkline(r.samples, Modifier.fillMaxWidth().height(60.dp))
            Text("${presenceText(r.presence)} - last heard ${ago(r.lastSeenMs)}", color = presenceColor(r.presence))
        }
        val flags = buildList {
            if ((r.rssi ?: -127) < -78) add("Weak signal at the moment")
            if (r.unstable) add("Signal is fluctuating by ${"%.1f".format(Rssi.stdDev(r.samples))} dB (std. deviation)")
            if (r.trend == "Degrading") add("Signal has been getting weaker")
            if (r.presence == DevicePresence.Missing) add("Not heard recently while the phone was able to hear")
            if (r.presence == DevicePresence.Uncertain) add("Cannot judge: the scanner was not running or had a gap")
        }
        if (flags.isNotEmpty()) Banner("Flags (observations, not a diagnosis)", flags.joinToString("\n"))
        Card2 {
            Label("Last 5 minutes vs earlier readings")
            val s = stats
            if (s == null || s.first == 0) Text("No stored readings in the last 5 minutes.", fontSize = 13.sp)
            else {
                Text("${s.first} stored windows, average ${"%.1f".format(s.second)} dBm", fontSize = 13.sp)
                val prev = s.third
                if (prev == null) Text("No earlier readings to compare with yet.", color = FleetColors.Muted, fontSize = 13.sp)
                else {
                    val d = (s.second ?: prev) - prev
                    Text("Earlier average ${"%.1f".format(prev)} dBm  (${if (d >= 0) "+" else ""}${"%.1f".format(d)} dB now)", fontSize = 13.sp,
                        color = if (d <= -6) FleetColors.Warn else FleetColors.Fg)
                }
            }
            Text("${r.observations} windows stored in total", color = FleetColors.Muted, fontSize = 12.sp)
        }
        r.advertisementHex?.let { Card2 { Label("Last advertisement (raw)"); Text(it, fontSize = 11.sp, color = FleetColors.Muted) } }
        OutlinedTextField(rename, { rename = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text("Name this device on this phone") })
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = { vm.rename(address, rename); rename = "" }, enabled = rename.isNotBlank()) { Text("Save name") }
            OutlinedButton(onClick = { onHistory(address) }) { Text("Signal history") }
            OutlinedButton(onClick = { onRadar(address) }) { Text("Show on radar") }
        }
    }
}

// ============================================================ 5. Signal history

@Composable
fun HistoryScreen(vm: FleetViewModel, initial: String?) {
    val rows by vm.rows.collectAsState()
    var address by remember(initial) { mutableStateOf(initial ?: rows.firstOrNull()?.address) }
    var periodMin by remember { mutableStateOf(60) }
    var menu by remember { mutableStateOf(false) }
    val device = rows.firstOrNull { it.address == address }
    val periods = listOf(15 to "15 min", 60 to "1 h", 360 to "6 h", 1440 to "24 h")
    val data by produceState<Triple<List<ChartPoint>, Long, String>?>(null, address, periodMin) {
        val a = address ?: return@produceState
        val to = System.currentTimeMillis(); val from = to - periodMin * 60_000L
        val obs = vm.history(a, from)
        val (pts, bucket) = bucketObservations(obs, from, to)
        val longest = obs.zipWithNext { p, n -> n.timestampMs - p.windowEndMs }.maxOrNull() ?: 0L
        value = Triple(pts, bucket, "${obs.size} windows | RSSI " + (if (obs.isEmpty()) "-" else "min ${obs.minOf { it.rssiMin }} / avg ${"%.1f".format(obs.map { it.rssiAvg }.average())} / max ${obs.maxOf { it.rssiMax }} dBm") +
            " | longest silent interval ${longest / 1000} s")
    }
    Screen("Signal history") {
        Box {
            OutlinedButton(onClick = { menu = true }) { Text(device?.name ?: "Choose a device") }
            DropdownMenu(menu, { menu = false }) {
                rows.sortedBy { it.name.lowercase() }.forEach { d -> DropdownMenuItem(text = { Text(d.name) }, onClick = { address = d.address; menu = false }) }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) { periods.forEach { (m, l) -> FilterChip(periodMin == m, { periodMin = m }, label = { Text(l) }) } }
        val d = data
        if (address == null) Text("No devices yet.", color = FleetColors.Muted)
        else if (d != null) {
            val to = System.currentTimeMillis()
            RssiChart(d.first, to - periodMin * 60_000L, to, d.second * 3, Modifier.fillMaxWidth().height(260.dp))
            Text(d.third, fontSize = 12.sp)
            Text("Breaks in the line mean nothing was heard. That is not proof the device disconnected. Windows are averaged; grey bars show each window's min-max range.", color = FleetColors.Muted, fontSize = 12.sp)
        }
    }
}

// ============================================================ 6. Inspection sessions

private val LOCATIONS = listOf("Office", "Studio 01", "Studio 02", "Studio 03", "Server area", "Storage room", "Custom")

@Composable
fun SessionsScreen(vm: FleetViewModel, onOpen: (String) -> Unit) {
    val ctx = LocalContext.current
    val active by vm.activeSession.collectAsState()
    val inspection by vm.inspection.collectAsState()
    val sessions by vm.sessions.collectAsState()
    val summaries by vm.sessionSummaries.collectAsState()
    val settings by vm.settings.collectAsState()
    val status by vm.status.collectAsState()
    var location by remember { mutableStateOf(if (settings.lastLocation in LOCATIONS) settings.lastLocation else "Office") }
    var custom by remember { mutableStateOf("") }
    var notes by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }

    Screen("Inspection sessions") {
        error?.let { Banner("Could not start", it) }
        if (active == null) {
            Card2 {
                Label("Where are you?")
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        LOCATIONS.chunked(3).forEach { chunk -> Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) { chunk.forEach { l -> FilterChip(location == l, { location = l }, label = { Text(l) }) } } }
                    }
                }
                if (location == "Custom") OutlinedTextField(custom, { custom = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text("Location name") })
                OutlinedTextField(notes, { notes = it }, Modifier.fillMaxWidth().padding(top = 6.dp), label = { Text("Notes (optional)") })
                Spacer(Modifier.height(8.dp))
                Button(onClick = {
                    val label = if (location == "Custom") custom.trim() else location
                    val pre = vm.engine.preflightError()
                    if (label.isBlank()) { error = "Enter a location name." }
                    else if (pre != null) { error = pre; launcher.launch(Permissions.requestable()) }
                    else { error = null; vm.startInspection(label, notes) { ScanService.start(ctx, ScanMode.Inspection) } }
                }) { Text("Start inspection") }
            }
        } else {
            val s = inspection
            Card2 {
                Text("Inspecting: ${active!!.second}", fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
                Text("Scanner: ${status.state.name}", color = FleetColors.Muted, fontSize = 13.sp)
                if (s != null) {
                    Text("Started ${fmtTime(s.startedMs)}  |  ${s.heard.size} devices heard  |  ${s.notHeard.size} known but not heard yet  |  ${s.unstable.size} unstable  |  ${s.weak.size} weak", fontSize = 13.sp)
                }
                OutlinedTextField(notes, { notes = it }, Modifier.fillMaxWidth().padding(top = 6.dp), label = { Text("Inspection notes") })
                Spacer(Modifier.height(8.dp))
                Button(onClick = { vm.endInspection(notes) { ScanService.stop(ctx); notes = "" } }) { Text("End and save inspection") }
            }
            if (s != null) {
                if (s.unstable.isNotEmpty()) { Label("Unstable right now (${s.unstable.size})"); s.unstable.take(10).forEach { DeviceCard(it) { onOpen(it.address) } }; MoreNote(s.unstable.size, 10) }
                if (s.weak.isNotEmpty()) { Label("Weak signal (${s.weak.size})"); s.weak.take(10).forEach { DeviceCard(it) { onOpen(it.address) } }; MoreNote(s.weak.size, 10) }
                if (s.notHeard.isNotEmpty()) {
                    Label("Known from earlier but not heard in this inspection")
                    Text("This only means nothing was heard here yet; they may be switched off, out of range, or still to come into range.", color = FleetColors.Muted, fontSize = 12.sp)
                    s.notHeard.take(10).forEach { DeviceCard(it) { onOpen(it.address) } }
                    MoreNote(s.notHeard.size, 10)
                }
            }
        }

        Label("Saved sessions")
        if (sessions.isEmpty()) Text("No sessions yet.", color = FleetColors.Muted)
        sessions.forEach { s ->
            val sum = summaries.firstOrNull { it.sessionId == s.id }
            Card2 {
                Text("${s.locationLabel}  -  ${fmtTime(s.startedAtMs, "dd MMM HH:mm")}", fontWeight = FontWeight.SemiBold)
                Text(
                    (s.endedAtMs?.let { "Lasted ${(it - s.startedAtMs) / 60000} min" } ?: "Still open") +
                        "  |  ${sum?.distinctDevices ?: 0} devices  |  ${sum?.observations ?: 0} windows", color = FleetColors.Muted, fontSize = 12.sp,
                )
                s.notes?.let { Text(it, fontSize = 12.sp) }
            }
        }
    }
}

@Composable
private fun MoreNote(total: Int, shown: Int) { if (total > shown) Text("+ ${total - shown} more (see the Nearby tab and filter by Weak / Unstable / Not heard)", color = FleetColors.Muted, fontSize = 12.sp) }

// ============================================================ 7. Sync status

@Composable
fun SyncScreen(vm: FleetViewModel) {
    val total by vm.observationCount.collectAsState()
    val pending by vm.pendingUploads.collectAsState()
    Screen("Sync status") {
        Card2 {
            Label("Windows server"); Text("Not paired", fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            Text("Pairing by QR code and uploading to the Windows app arrive in Phase 3. Nothing leaves this phone until then.", color = FleetColors.Muted, fontSize = 13.sp)
        }
        Card2 {
            Label("Stored on this phone"); Text("$total windows", fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            Label("Waiting to be uploaded"); Text("$pending windows", fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            Text("Windows that were never acknowledged by the server are never deleted automatically.", color = FleetColors.Muted, fontSize = 12.sp)
        }
    }
}

// ============================================================ 8. Settings

@Composable
fun SettingsScreen(vm: FleetViewModel) {
    val ctx = LocalContext.current
    val s by vm.settings.collectAsState()
    val phone by rememberPhoneState()
    Screen("Settings") {
        Card2 {
            Label("Sampling: one stored window per device every ... seconds")
            Text("Every advertisement is counted and the window keeps last/min/max/average RSSI. 0 stores every advertisement (fills storage quickly).", color = FleetColors.Muted, fontSize = 12.sp)
            NumberField("Continuous", s.sampleContinuousSec) { v -> vm.updateSettings { it.copy(sampleContinuousSec = v) } }
            NumberField("Inspection", s.sampleInspectionSec) { v -> vm.updateSettings { it.copy(sampleInspectionSec = v) } }
            NumberField("Background", s.sampleBackgroundSec) { v -> vm.updateSettings { it.copy(sampleBackgroundSec = v) } }
            Text("Changes apply the next time scanning starts.", color = FleetColors.Muted, fontSize = 12.sp)
        }
        Card2 {
            NumberField("Presence window (seconds)", s.presenceWindowSec) { v -> vm.updateSettings { it.copy(presenceWindowSec = v) } }
            Text("A device not heard for longer than this shows as \"Not heard recently\", only while the scanner could hear.", color = FleetColors.Muted, fontSize = 12.sp)
        }
        Card2 {
            Label("Background mode: exact device names to watch (comma separated)")
            OutlinedTextField(s.backgroundDeviceNames, { v -> vm.updateSettings { it.copy(backgroundDeviceNames = v) } }, Modifier.fillMaxWidth(), singleLine = true, placeholder = { Text("e.g. LVS-Lush, LVS-Z14") })
            Text("Android only keeps filtered scans running with the screen off. Leave empty to scan everything while the screen is on.", color = FleetColors.Muted, fontSize = 12.sp)
        }
        Card2 {
            Label("Radar accuracy")
            Text("Path-loss exponent x10: 20 is open air, 25 to 30 is typical indoors, 35 or more in cluttered rooms. Calibrating a device matters more than this number.", color = FleetColors.Muted, fontSize = 12.sp)
            NumberField("Path-loss exponent x10", s.pathLossTenths) { v -> vm.updateSettings { it.copy(pathLossTenths = v) } }
            NumberField("Step length (cm), for walking tracking", s.strideCm) { v -> vm.updateSettings { it.copy(strideCm = v) } }
        }
        Card2 {
            Label("Battery")
            Text(if (phone.battery) "Unrestricted: good for long scans." else "Optimised: Android may stop scanning in the background.", fontSize = 13.sp)
            OutlinedButton(onClick = { requestBattery(ctx) }) { Text("Allow background use") }
        }
        Card2 {
            Label("Demo data")
            Row(Modifier.fillMaxWidth().clickable { vm.updateSettings { it.copy(demoMode = !it.demoMode) } }, verticalAlignment = Alignment.CenterVertically) {
                Switch(s.demoMode, { v -> vm.updateSettings { it.copy(demoMode = v) } })
                Text("  Show fictional devices (for screenshots)", fontSize = 14.sp)
            }
            Text("Replaces the real devices on the radar and in the lists with made-up ones, so the app can be photographed without showing real surroundings. A DEMO DATA banner stays on every screen while it is on. Nothing is stored or uploaded.", color = FleetColors.Muted, fontSize = 12.sp)
        }
        Card2 {
            Label("About"); Text("Bluetooth Fleet Monitor (mobile) 0.2.1")
            Text("Local only: no ads, no analytics, no cloud. Needs no internet access in this version.", color = FleetColors.Muted, fontSize = 12.sp)
        }
    }
}

@Composable
private fun NumberField(label: String, value: Int, onChange: (Int) -> Unit) {
    var text by remember(value) { mutableStateOf(value.toString()) }
    OutlinedTextField(text, { t -> text = t.filter { it.isDigit() }.take(5); text.toIntOrNull()?.let(onChange) }, Modifier.fillMaxWidth().padding(top = 6.dp),
        singleLine = true, label = { Text(label) }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
}

// ============================================================ More

@Composable
fun MoreScreen(vm: FleetViewModel, onHistory: () -> Unit, onSync: () -> Unit, onSettings: () -> Unit) {
    val events by vm.events.collectAsState()
    Screen("More") {
        Button(onClick = onHistory, modifier = Modifier.fillMaxWidth()) { Text("Signal history") }
        Button(onClick = onSync, modifier = Modifier.fillMaxWidth()) { Text("Sync status") }
        Button(onClick = onSettings, modifier = Modifier.fillMaxWidth()) { Text("Settings") }
        Label("Event log")
        if (events.isEmpty()) Text("No events yet.", color = FleetColors.Muted)
        events.take(40).forEach { e ->
            Text("${fmtTime(e.timestampMs, "dd MMM HH:mm:ss")}  ${e.severity}  ${e.type}: ${e.message}", fontSize = 12.sp,
                color = if (e.severity == "Info") FleetColors.Muted else FleetColors.Warn)
        }
    }
}
