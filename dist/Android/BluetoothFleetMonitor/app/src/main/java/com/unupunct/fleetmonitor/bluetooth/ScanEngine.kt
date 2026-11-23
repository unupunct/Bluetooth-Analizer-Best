package com.unupunct.fleetmonitor.bluetooth

import android.app.PendingIntent
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.le.BluetoothLeScanner
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.PowerManager
import com.unupunct.fleetmonitor.data.DeviceFacts
import com.unupunct.fleetmonitor.data.FleetRepository
import com.unupunct.fleetmonitor.data.ScanMode
import com.unupunct.fleetmonitor.data.SettingsStore
import com.unupunct.fleetmonitor.domain.*
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.concurrent.atomic.AtomicLong
import javax.inject.Inject
import javax.inject.Singleton

/** What the UI shows for a device heard during this run. */
class LiveDevice(
    val address: String,
    val name: String?,
    val manufacturer: String?,
    val rssi: Int,
    val lastSeenMs: Long,
    val samples: List<Int>,
    val advCount: Long,
    val advertisementHex: String?,
)

class Counters(val heard: Long = 0, val stored: Long = 0, val dropped: Long = 0, val lost: Long = 0)

/**
 * Owns scanning for the whole app: BluetoothLeScanner -> bounded queue -> per-address sampling windows ->
 * batched Room writes, plus honest status, coverage gaps and interruption handling. The same pipeline serves the
 * three modes; only the scan settings, sampling window and the source of results differ.
 */
@Singleton
class ScanEngine @Inject constructor(
    @ApplicationContext private val context: Context,
    private val repo: FleetRepository,
    private val settings: SettingsStore,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val health = ScanHealth(noAdvertisementMs = 20_000)
    private val _status = MutableStateFlow(health.current())
    private val _live = MutableStateFlow<List<LiveDevice>>(emptyList())
    private val _counters = MutableStateFlow(Counters())
    private val _mode = MutableStateFlow(settings.scanMode)
    private val _session = MutableStateFlow<Pair<String, String>?>(null) // id to location label

    val status: StateFlow<ScanStatus> = _status.asStateFlow()
    val live: StateFlow<List<LiveDevice>> = _live.asStateFlow()
    val counters: StateFlow<Counters> = _counters.asStateFlow()
    val mode: StateFlow<ScanMode> = _mode.asStateFlow()
    val activeSession: StateFlow<Pair<String, String>?> = _session.asStateFlow()

    private val _scanScope = MutableStateFlow(SCOPE_ALL)
    /** What the scan can currently hear. Android pauses open-ended scans when the screen is off, so the scope narrows then. */
    val scanScope: StateFlow<String> = _scanScope.asStateFlow()

    private val heard = AtomicLong()
    private val stored = AtomicLong()
    private val dropped = AtomicLong()
    private val lost = AtomicLong()
    private var droppedReported = 0L

    private var channel = Channel<BleAdvertisement>(QUEUE_CAPACITY)
    private var processor: Job? = null
    private var retry: Job? = null
    private var userWantsScan = false
    private var running = false
    private var aggregator = ObservationAggregator(10_000)
    private val liveState = HashMap<String, LiveState>()
    private val facts = HashMap<String, DeviceFacts>()

    private class LiveState(var name: String?, var manufacturer: String?, var rssi: Int, var lastSeenMs: Long, var advCount: Long, var hex: String?) {
        val samples = ArrayDeque<Int>()
        var lastSampleMs = 0L
    }

    private val adapter: BluetoothAdapter? get() = (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    private val scanner: BluetoothLeScanner? get() = adapter?.bluetoothLeScanner

    init {
        recoverFromInterruptedRun()
    }

    /** If the last run said it was scanning but the process then died (or the phone restarted), record that as a gap. */
    private fun recoverFromInterruptedRun() {
        val savedMode = settings.scanMode
        if (!settings.wantScanning) return
        if (savedMode == ScanMode.Background) {
            // Background scans are owned by Android and survive process death; results arrive through the receiver.
            running = true; userWantsScan = true
            health.started()
            publishStatus()
            scope.launch { reRegisterBackgroundScan() }
            return
        }
        val last = settings.heartbeatMs
        val now = System.currentTimeMillis()
        settings.wantScanning = false
        // Any unclean end counts. The heartbeat is written every 10 s, so the true start of the gap is at most 10 s after `last`;
        // we record from `last` (the earliest it could have started) rather than hide a short outage behind a grace period.
        if (last > 0) {
            val reason = "The app was not scanning (process stopped or phone restarted)"
            scope.launch {
                repo.addPastGap(last, now, reason)
                repo.recordEvent("Warning", "ScanInterrupted", "Scanning stopped unexpectedly at about ${java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date(last))}. Open the Scanner tab to resume.")
            }
            health.recordPastGap(last, now, reason)
        }
        health.stopped(requestedByUser = false, reason = "the app or the phone restarted while scanning")
        publishStatus()
    }

    // ------------------------------------------------------------------ control

    fun setActiveSession(id: String?, location: String?) { _session.value = if (id != null && location != null) id to location else null }

    /** Why a scan cannot start right now, or null if it can. */
    fun preflightError(): String? = when {
        !Permissions.hasBluetoothLe(context) -> "This phone has no Bluetooth Low Energy."
        !Permissions.hasScanPermission(context) -> "Scan permission has not been granted."
        !Permissions.bluetoothEnabled(context) -> "Bluetooth is turned off."
        !Permissions.locationServicesOn(context) -> "Android 10 and 11 need the Location setting switched on to scan for Bluetooth."
        else -> null
    }

    /** Starts scanning in [newMode]. Returns null on success or a human-readable reason it could not start. */
    @Synchronized
    fun start(newMode: ScanMode): String? {
        if (running && _mode.value == newMode) return null
        if (running) stop(byUser = true)
        preflightError()?.let { return it }

        _mode.value = newMode
        settings.scanMode = newMode
        settings.wantScanning = true
        userWantsScan = true
        beginRun()
        val error = startScanning()
        if (error != null) {
            interrupt(error)
            return error
        }
        if (newMode != ScanMode.Background && !(context.getSystemService(Context.POWER_SERVICE) as PowerManager).isInteractive)
            scope.launch { narrowToKnownDevices() }
        return null
    }

    @Synchronized
    fun stop(byUser: Boolean = true) {
        if (byUser) { userWantsScan = false; settings.wantScanning = false }
        retry?.cancel()
        stopScanning()
        unregisterBluetoothReceiver()
        _scanScope.value = SCOPE_ALL
        if (running) {
            running = false
            health.stopped(requestedByUser = byUser, reason = if (byUser) null else "stopped")
        }
        // Capture this run's queue and windows now: a quick restart must not make the flush read the next run's objects.
        val p = processor; val ch = channel; val agg = aggregator
        processor = null
        scope.launch { flushAndStopProcessor(p, ch, agg) }
        publishStatus()
    }

    private fun beginRun() {
        running = true
        health.started()
        scope.launch { repo.closeGaps(System.currentTimeMillis()) }
        channel = Channel(QUEUE_CAPACITY)
        aggregator = ObservationAggregator(settings.sampleIntervalMs(_mode.value))
        settings.heartbeatMs = System.currentTimeMillis()
        registerBluetoothReceiver()
        startProcessor()
        publishStatus()
    }

    private fun interrupt(reason: String) {
        stopScanning()
        if (running) {
            running = false
            health.stopped(requestedByUser = false, reason = reason)
            val now = System.currentTimeMillis()
            scope.launch {
                repo.openGap(now, reason)
                repo.recordEvent("Warning", "ScanInterrupted", reason)
            }
        }
        publishStatus()
    }

    private fun resumeAfterInterruption() {
        synchronized(this) {
            if (running || !userWantsScan) return
            if (!Permissions.bluetoothEnabled(context) || !Permissions.hasScanPermission(context)) return
            beginRun()
            val error = startScanning()
            if (error != null) { interrupt(error); scheduleRetry() }
            else scope.launch { repo.recordEvent("Info", "ScanResumed", "Scanning resumed automatically") }
        }
    }

    private fun scheduleRetry() {
        retry?.cancel()
        retry = scope.launch {
            delay(30_000)
            if (userWantsScan && !running) resumeAfterInterruption()
        }
    }

    // ------------------------------------------------------------------ BLE

    private val callback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) = ingest(result)
        override fun onBatchScanResults(results: MutableList<ScanResult>) = results.forEach(::ingest)
        override fun onScanFailed(errorCode: Int) {
            interrupt("Scan failed: ${ScanMapping.scanFailureText(errorCode)}")
            scheduleRetry()
        }
    }

    private fun ingest(r: ScanResult) {
        val ad = ScanMapping.toAdvertisement(r) ?: return
        enqueue(ad)
    }

    /** Called from the background-scan receiver, possibly in a freshly started process. */
    fun ingestBackground(results: List<ScanResult>) {
        synchronized(this) {
            // A process Android recreated just to deliver results has no processing loop yet, even if recovery already
            // marked the scan as running. Without this the results would pile up in a queue nobody empties.
            if (processor?.isActive != true) {
                if (!running) { running = true; userWantsScan = true; _mode.value = ScanMode.Background; health.started() }
                channel = Channel(QUEUE_CAPACITY)
                aggregator = ObservationAggregator(settings.sampleIntervalMs(ScanMode.Background))
                startProcessor()
            }
        }
        results.forEach(::ingest)
    }

    /**
     * The OS keeps a PendingIntent scan across process death, but not across an app update or a force-stop, and there is
     * no API to ask whether it is still registered. So whenever the app restarts in Background mode it registers the scan
     * again (stop then start with the same PendingIntent), instead of claiming coverage it may not have.
     */
    private fun reRegisterBackgroundScan() {
        synchronized(this) {
            if (!userWantsScan || _mode.value != ScanMode.Background) return
            val problem = preflightError()
            if (problem != null) { interrupt(problem); return }
            stopScanning()
            val error = startScanning()
            if (error != null) { interrupt(error); scheduleRetry() } else _scanScope.value = describeBackgroundScope()
        }
    }

    private fun enqueue(ad: BleAdvertisement) {
        health.advertisementReceived()
        heard.incrementAndGet()
        val sent = channel.trySend(ad)
        // An advertisement arriving after the queue was closed by a stop is not an overflow; only a full queue is.
        if (sent.isFailure && !sent.isClosed) dropped.incrementAndGet()
    }

    private fun startScanning(): String? {
        val s = scanner ?: return "Bluetooth scanner is not available."
        return try {
            if (_mode.value == ScanMode.Background) {
                val code = s.startScan(backgroundFilters(), backgroundSettings(), backgroundIntent())
                if (code != 0) "Background scan could not start: ${ScanMapping.scanFailureText(code)}"
                else { _scanScope.value = describeBackgroundScope(); null }
            } else {
                s.startScan(null, lowLatency(), callback)
                null
            }
        } catch (e: SecurityException) {
            "Scan permission was revoked."
        } catch (e: IllegalStateException) {
            "Bluetooth is not ready: ${e.message}"
        }
    }

    private fun stopScanning() {
        val s = scanner ?: return
        try {
            if (_mode.value == ScanMode.Background) s.stopScan(backgroundIntent()) else s.stopScan(callback)
        } catch (_: SecurityException) { /* permission gone: nothing left to stop */ }
        catch (_: IllegalStateException) { /* adapter turned off underneath us */ }
    }

    private fun backgroundFilters(): List<ScanFilter> {
        val names = settings.settings.value.backgroundDeviceNames.split(',').map { it.trim() }.filter { it.isNotEmpty() }
        if (names.isNotEmpty()) return names.map { ScanFilter.Builder().setDeviceName(it).build() }
        // No names given: watch the devices already known. An empty filter would be an open-ended scan, which Android pauses with the screen off.
        val known = runBlocking { repo.allDevices().map { it.address } }.mapNotNull(::addressFilter)
        return known.ifEmpty { listOf(ScanFilter.Builder().build()) }
    }

    /** What the OS-managed background scan is filtered to, for the Scanner screen. */
    private fun describeBackgroundScope(): String {
        val names = settings.settings.value.backgroundDeviceNames.split(',').map { it.trim() }.filter { it.isNotEmpty() }
        if (names.isNotEmpty()) return "Background: only devices named ${names.joinToString(", ")}"
        val known = runBlocking { repo.allDevices() }.count { addressFilter(it.address) != null }
        return if (known > 0) "Background: watching $known known devices (Android keeps filtered scans running with the screen off)"
        else "Background: open-ended scan, which Android pauses when the screen is off (no devices known yet to filter on)"
    }

    private fun addressFilter(address: String): ScanFilter? {
        val colon = address.chunked(2).joinToString(":")
        return if (BluetoothAdapter.checkBluetoothAddress(colon)) ScanFilter.Builder().setDeviceAddress(colon).build() else null
    }

    private fun lowLatency() = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).setReportDelay(0).build()

    /** Screen went off: swap the open-ended scan for one filtered to known devices, which Android keeps running. */
    private suspend fun narrowToKnownDevices() {
        val addresses = (repo.allDevices().map { it.address } + _live.value.map { it.address }).toSet()
        var message: String? = null
        synchronized(this) {
            if (!running || _mode.value == ScanMode.Background) return
            stopScanning()
            val filters = addresses.mapNotNull(::addressFilter)
            if (filters.isEmpty()) {
                _scanScope.value = "Paused: the screen is off and no devices are known yet"
                message = "Screen off with no known devices: Android pauses open-ended scans, so nothing can be heard until the screen is on."
            } else {
                val error = try { scanner?.startScan(filters, lowLatency(), callback); null }
                    catch (e: SecurityException) { "Scan permission was revoked." }
                    catch (e: IllegalStateException) { "Bluetooth is not ready: ${e.message}" }
                if (error != null) { interrupt(error); return }
                _scanScope.value = "Screen off: watching ${filters.size} known devices only"
                message = "Screen off: scan narrowed to ${filters.size} known devices (Android pauses open-ended scans when the screen is off). New devices are not discovered until the screen is on."
            }
        }
        message?.let { repo.recordEvent("Info", "ScanScope", it) }
    }

    private suspend fun widenToAllDevices() {
        var failed: String? = null
        synchronized(this) {
            if (!running || _mode.value == ScanMode.Background) return
            stopScanning()
            failed = startScanning()
            if (failed != null) interrupt(failed!!) else _scanScope.value = SCOPE_ALL
        }
        if (failed == null) repo.recordEvent("Info", "ScanScope", "Screen on: scanning all nearby devices again")
    }

    private fun backgroundSettings() = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_POWER).setReportDelay(0).build()

    private fun backgroundIntent(): PendingIntent {
        val intent = Intent(context, BackgroundScanReceiver::class.java).setAction(BackgroundScanReceiver.ACTION)
        // Must be mutable: the Bluetooth stack adds the scan results to the intent as extras.
        return PendingIntent.getBroadcast(context, 1, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
    }

    // ------------------------------------------------------------------ Bluetooth on/off

    private var receiverRegistered = false
    private val btReceiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            when (i.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR)) {
                BluetoothAdapter.STATE_TURNING_OFF, BluetoothAdapter.STATE_OFF -> if (running) interrupt("Bluetooth was turned off")
                BluetoothAdapter.STATE_ON -> if (userWantsScan && !running) resumeAfterInterruption()
            }
        }
    }

    private val screenReceiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            if (!running || _mode.value == ScanMode.Background) return
            when (i.action) {
                Intent.ACTION_SCREEN_OFF -> scope.launch { narrowToKnownDevices() }
                Intent.ACTION_SCREEN_ON -> scope.launch { widenToAllDevices() }
            }
        }
    }

    private fun registerBluetoothReceiver() {
        if (receiverRegistered) return
        context.registerReceiver(btReceiver, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED))
        context.registerReceiver(screenReceiver, IntentFilter(Intent.ACTION_SCREEN_OFF).apply { addAction(Intent.ACTION_SCREEN_ON) })
        receiverRegistered = true
    }

    private fun unregisterBluetoothReceiver() {
        if (!receiverRegistered) return
        runCatching { context.unregisterReceiver(btReceiver) }
        runCatching { context.unregisterReceiver(screenReceiver) }
        receiverRegistered = false
    }

    // ------------------------------------------------------------------ processing

    private fun startProcessor() {
        processor?.cancel()
        val myChannel = channel
        val myAggregator = aggregator
        processor = scope.launch {
            val batch = ArrayList<AggregatedObservation>()
            var lastTick = 0L
            var lastHeartbeat = 0L
            while (isActive) {
                val first = withTimeoutOrNull(500) { myChannel.receiveCatching().getOrNull() }
                if (first != null) {
                    handle(first, myAggregator, batch)
                    var n = 0
                    while (n < 2000) { val next = myChannel.tryReceive().getOrNull() ?: break; handle(next, myAggregator, batch); n++ }
                }
                val now = System.currentTimeMillis()
                if (now - lastTick >= 1000) {
                    lastTick = now
                    batch += myAggregator.flushDue(now)
                    persist(batch)
                    publishLive(now)
                    reportDropped()
                    publishStatus()
                    if (now - lastHeartbeat >= 10_000) { lastHeartbeat = now; if (_mode.value != ScanMode.Background) settings.heartbeatMs = now }
                }
            }
        }
    }

    private suspend fun flushAndStopProcessor(p: Job?, ch: Channel<BleAdvertisement>, agg: ObservationAggregator) {
        if (p == null) return
        // Let the run drain what is already queued, then close the open windows so nothing in memory is lost.
        ch.close()
        val remaining = ArrayList<AggregatedObservation>()
        p.cancelAndJoin()
        var ad = ch.tryReceive().getOrNull()
        while (ad != null) { handle(ad, agg, remaining); ad = ch.tryReceive().getOrNull() }
        remaining += agg.flushAll()
        persist(remaining)
        publishLive(System.currentTimeMillis())
        reportDropped()
    }

    private fun handle(ad: BleAdvertisement, agg: ObservationAggregator, batch: MutableList<AggregatedObservation>) {
        val st = liveState.getOrPut(ad.address) { LiveState(ad.name, null, ad.rssi, ad.observedAtMs, 0, null) }
        st.advCount++
        if (ad.observedAtMs >= st.lastSeenMs) { st.rssi = ad.rssi; st.lastSeenMs = ad.observedAtMs }
        if (!ad.name.isNullOrBlank()) st.name = ad.name
        if (ad.manufacturer.isNotEmpty() && st.manufacturer == null) st.manufacturer = CompanyIds.describe(ad.manufacturer[0].companyId)
        if (ad.payload.isNotEmpty()) st.hex = Fingerprint.hex(ad.payload)
        if (ad.observedAtMs - st.lastSampleMs >= 1000) { // one sparkline sample per second per device
            st.lastSampleMs = ad.observedAtMs
            st.samples.addLast(ad.rssi); while (st.samples.size > SAMPLE_CAP) st.samples.removeFirst()
        }
        if (!facts.containsKey(ad.address))
            facts[ad.address] = DeviceFacts(st.manufacturer, Fingerprint.compute(ad.manufacturer, ad.serviceUuids), ad.addressKind.name)
        agg.offer(ad)?.let { batch += it }
    }

    private suspend fun persist(batch: MutableList<AggregatedObservation>) {
        if (batch.isEmpty()) return
        val rows = batch.toList(); batch.clear()
        val session = _session.value
        for (attempt in 1..3) {
            try {
                stored.addAndGet(repo.saveBatch(rows, facts, session?.first, session?.second, _mode.value).toLong())
                return
            } catch (e: Exception) {
                if (attempt == 3) {
                    lost.addAndGet(rows.size.toLong())
                    runCatching { repo.recordEvent("Error", "WriteFailed", "Could not store ${rows.size} observation windows: ${e.message}") }
                } else delay(100L * attempt)
            }
        }
    }

    private suspend fun reportDropped() {
        val total = dropped.get()
        if (total > droppedReported) {
            val delta = total - droppedReported
            droppedReported = total
            runCatching { repo.recordEvent("Warning", "QueueOverflow", "$delta advertisements were dropped because the queue was full. Total dropped: $total.") }
        }
    }

    private fun publishLive(now: Long) {
        _live.value = liveState.map { (addr, s) ->
            LiveDevice(addr, s.name, s.manufacturer, s.rssi, s.lastSeenMs, s.samples.toList(), s.advCount, s.hex)
        }
        _counters.value = Counters(heard.get(), stored.get(), dropped.get(), lost.get())
    }

    private fun publishStatus() { _status.value = health.current() }

    /** Coverage gaps held in memory for presence decisions (the database copy is the permanent record). */
    fun coverageGaps(): List<CoverageGap> = health.gaps

    companion object {
        const val SCOPE_ALL = "All nearby devices"
        private const val QUEUE_CAPACITY = 20_000
        private const val SAMPLE_CAP = 60
    }
}
