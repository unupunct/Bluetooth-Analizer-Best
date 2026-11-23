package com.unupunct.fleetmonitor.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.unupunct.fleetmonitor.bluetooth.GattResult
import com.unupunct.fleetmonitor.bluetooth.GattTester
import com.unupunct.fleetmonitor.bluetooth.MotionTracker
import com.unupunct.fleetmonitor.bluetooth.ScanEngine
import com.unupunct.fleetmonitor.data.*
import com.unupunct.fleetmonitor.domain.*
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import javax.inject.Inject
import kotlin.math.roundToInt

class DeviceRow(
    val address: String,
    val name: String,
    val manufacturer: String?,
    val rssi: Int?,
    val quality: String,
    val trend: String,
    val unstable: Boolean,
    val lastSeenMs: Long?,
    val presence: DevicePresence,
    val heardThisRun: Boolean,
    val samples: List<Int>,
    val observations: Long,
    val advertisementHex: String?,
    val firstSeenMs: Long?,
    // ---- identification and ranging (added for the radar) ----
    val classification: Classification = Classification(Classification.UNKNOWN, 0.0, null, emptyList()),
    val range: RangeEstimate? = null,
    val connectable: Boolean? = null,
    val txPower: Int? = null,
    val calibRssi1m: Int? = null,
    val gattName: String? = null,
    val gattManufacturer: String? = null,
    val gattModel: String? = null,
    val gattFirmware: String? = null,
    val gattCheckedMs: Long? = null,
)

class InspectionSummary(
    val sessionId: String,
    val location: String,
    val startedMs: Long,
    val heard: List<DeviceRow>,
    /** Known from earlier scans but not heard since this inspection started. Not proof that they are off. */
    val notHeard: List<DeviceRow>,
    val unstable: List<DeviceRow>,
    val weak: List<DeviceRow>,
)

/** State of the "Connect and test" action for the selected device. */
sealed class GattUi {
    object Idle : GattUi()
    class Running(val address: String) : GattUi()
    class Done(val address: String, val result: GattResult) : GattUi()
}

@HiltViewModel
class FleetViewModel @Inject constructor(
    val engine: ScanEngine,
    val repo: FleetRepository,
    private val settingsStore: SettingsStore,
    val motion: MotionTracker,
    private val gatt: GattTester,
    val sync: com.unupunct.fleetmonitor.sync.SyncManager,
) : ViewModel() {

    val status = engine.status
    val counters = engine.counters
    val mode = engine.mode
    val activeSession = engine.activeSession
    val settings = settingsStore.settings

    val observationCount = repo.observationCount.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0L)
    val pendingUploads = repo.pendingUploadCount.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0L)
    val sessions = repo.sessions.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    val sessionSummaries = repo.sessionSummaries.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    val gaps = repo.gaps.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())
    val events = repo.events.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

    private val ticker = flow { while (true) { emit(System.currentTimeMillis()); delay(1000) } }

    private val liveRows: StateFlow<List<DeviceRow>> = combine(repo.devices, engine.live, settingsStore.settings, engine.status, ticker) { db, live, s, st, now ->
        val liveBy = live.associateBy { it.address }
        val persistedGaps = gaps.value.map { CoverageGap(it.startMs, it.endMs, it.reason) }
        val allGaps = (persistedGaps + engine.coverageGaps()).distinct()
        val window = s.presenceWindowSec * 1000L
        val running = st.state == ScannerState.Running
        val n = s.pathLossTenths / 10.0

        fun build(address: String, d: DeviceEntity?, l: com.unupunct.fleetmonitor.bluetooth.LiveDevice?): DeviceRow {
            val last = maxOf(d?.lastSeenMs ?: 0L, l?.lastSeenMs ?: 0L)
            val rssi = if (l != null && (d == null || l.lastSeenMs >= d.lastSeenMs)) l.rssi else d?.lastRssi
            val samples = l?.samples ?: emptyList()
            val liveName = l?.name?.takeIf { it.isNotBlank() }
            val name = d?.customName?.takeIf { it.isNotBlank() } ?: liveName ?: d?.advertisedName?.takeIf { it.isNotBlank() }
                ?: d?.gattName?.takeIf { it.isNotBlank() } ?: "Device ${address.takeLast(6)}"
            val companyIds = (l?.companyIds ?: emptySet()) + (d?.companyIds?.split(',')?.mapNotNull { it.trim().toIntOrNull() } ?: emptyList())
            val services = (l?.services16 ?: emptySet()) + (d?.serviceUuids?.split(',')?.mapNotNull { it.trim().toIntOrNull(16) } ?: emptyList())
            val appearance = l?.appearance ?: d?.appearance
            val classifyName = liveName ?: d?.advertisedName ?: d?.gattName
            val cls = DeviceClassifier.classify(classifyName, companyIds, services, appearance)
            val range = if (l != null && presenceOk(last, now, window))
                (Ranging.estimate(samples, d?.calibRssi1m, l.txPower, n) ?: Ranging.provisional(l.rssi.toDouble(), d?.calibRssi1m, l.txPower, n)) else null
            return DeviceRow(
                address = address, name = name,
                manufacturer = d?.manufacturer ?: l?.manufacturer, rssi = rssi, quality = rssi?.let { Rssi.describe(it) } ?: "",
                trend = Rssi.trend(samples), unstable = Rssi.isUnstable(samples), lastSeenMs = last,
                presence = PresenceStatus.evaluate(last, now, window, running, allGaps), heardThisRun = l != null, samples = samples,
                observations = d?.observationCount ?: 0, advertisementHex = l?.advertisementHex, firstSeenMs = d?.firstSeenMs ?: l?.lastSeenMs,
                classification = cls, range = range, connectable = l?.connectable, txPower = l?.txPower, calibRssi1m = d?.calibRssi1m,
                gattName = d?.gattName, gattManufacturer = d?.gattManufacturer, gattModel = d?.gattModel, gattFirmware = d?.gattFirmware, gattCheckedMs = d?.gattCheckedMs,
            )
        }

        val dbAddresses = db.map { it.address }.toSet()
        db.map { build(it.address, it, liveBy[it.address]) } + live.filter { it.address !in dbAddresses }.map { build(it.address, null, it) }
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

    /** Real rows, or fictional ones while Demo data is on in Settings (for screenshots). */
    val rows: StateFlow<List<DeviceRow>> = combine(liveRows, settingsStore.settings, ticker) { real, s, now ->
        if (s.demoMode) DemoData.rows(now, s.pathLossTenths / 10.0) else real
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

    private fun presenceOk(last: Long, now: Long, window: Long) = now - last <= window

    val inspection: StateFlow<InspectionSummary?> = combine(rows, engine.activeSession, repo.sessions) { rs, active, sessions ->
        val a = active ?: return@combine null
        val s = sessions.firstOrNull { it.id == a.first } ?: return@combine null
        val heard = rs.filter { (it.lastSeenMs ?: 0) >= s.startedAtMs }
        val notHeard = rs.filter { (it.firstSeenMs ?: Long.MAX_VALUE) < s.startedAtMs && (it.lastSeenMs ?: 0) < s.startedAtMs }
        InspectionSummary(
            sessionId = s.id, location = s.locationLabel, startedMs = s.startedAtMs, heard = heard, notHeard = notHeard,
            unstable = heard.filter { it.unstable }, weak = heard.filter { (it.rssi ?: -127) < -78 },
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), null)

    // ------------------------------------------------------------------ radar: selection, locating, calibration, connect test

    private val _selected = MutableStateFlow<String?>(null)
    val selected: StateFlow<String?> = _selected.asStateFlow()
    fun select(address: String?) { _selected.value = address }

    private val _target = MutableStateFlow<String?>(null)
    /** The device the user is searching for. The radar highlights it and dims everything else. */
    val target: StateFlow<String?> = _target.asStateFlow()
    fun setTarget(address: String?) {
        _target.value = address
        if (address != null) _selected.value = address else if (_locating.value != null) stopLocate()
    }

    private val _watch = MutableStateFlow("")
    /** Text to watch for: the first device heard whose name, maker or type matches becomes the target. */
    val watch: StateFlow<String> = _watch.asStateFlow()
    fun setWatch(text: String) { _watch.value = text.trim() }

    private val _locating = MutableStateFlow<String?>(null)
    /** The device currently being located by walking, if any. */
    val locating: StateFlow<String?> = _locating.asStateFlow()
    private val _estimate = MutableStateFlow<LocationEstimate?>(null)
    val locateEstimate: StateFlow<LocationEstimate?> = _estimate.asStateFlow()
    private val _walkSamples = MutableStateFlow(0)
    val walkSamples: StateFlow<Int> = _walkSamples.asStateFlow()
    private var locateJob: Job? = null
    private val path = ArrayList<PathSample>()

    fun startLocate(address: String) {
        stopLocate()
        motion.start(settingsStore.settings.value.strideCm / 100.0)
        motion.resetPath(); path.clear(); _estimate.value = null; _walkSamples.value = 0
        _locating.value = address; _selected.value = address; _target.value = address
        locateJob = viewModelScope.launch {
            while (true) {
                delay(1000)
                val row = rows.value.firstOrNull { it.address == address } ?: continue
                val r = row.range ?: continue
                val m = motion.state.value
                if (!m.hasHeading) continue
                path.add(PathSample(m.x, m.y, r.smoothedRssi))
                if (path.size > 150) path.removeAt(0)
                _walkSamples.value = path.size
                val s = settingsStore.settings.value
                val (ref, _) = Ranging.reference(row.calibRssi1m, row.txPower)
                _estimate.value = DeviceLocator.estimate(path.toList(), ref, s.pathLossTenths / 10.0)
            }
        }
    }

    fun stopLocate() { locateJob?.cancel(); locateJob = null; _locating.value = null }

    /** "I am [knownMeters] from this device right now": stores the signal level that distance corresponds to. Returns false if there is not enough signal data yet. */
    fun calibrate(address: String, knownMeters: Double): Boolean {
        val row = rows.value.firstOrNull { it.address == address } ?: return false
        val smooth = Ranging.smooth(row.samples.takeLast(15)) ?: return false
        if (row.samples.size < 5) return false
        val ref = Ranging.referenceFromKnownDistance(smooth, knownMeters, settingsStore.settings.value.pathLossTenths / 10.0).roundToInt()
        viewModelScope.launch { repo.setCalibration(address, ref) }
        return true
    }

    fun clearCalibration(address: String) { viewModelScope.launch { repo.setCalibration(address, null) } }

    private val _gattUi = MutableStateFlow<GattUi>(GattUi.Idle)
    val gattUi: StateFlow<GattUi> = _gattUi.asStateFlow()

    fun gattHistory(address: String) = repo.connectionTests(address)

    /** Runs one connect-and-read test. Only ever called from an explicit tap on a chosen device, after the warning. */
    fun runConnectTest(address: String) {
        if (_gattUi.value is GattUi.Running) return
        _gattUi.value = GattUi.Running(address)
        viewModelScope.launch {
            val started = System.currentTimeMillis()
            val r = gatt.test(address)
            if (r.outcome != "Cooldown" && r.outcome != "PermissionDenied") {
                val rs = r.rssiReads
                repo.logConnectionTest(ConnectionTestEntity(
                    address = address, startedMs = started, durationMs = r.durationMs, outcome = r.outcome, gattStatus = r.gattStatus, detail = r.detail,
                    services = r.services.joinToString(";").ifEmpty { null }, rssiMin = rs.minOrNull(), rssiMax = rs.maxOrNull(),
                    rssiAvg = if (rs.isEmpty()) null else rs.average(), rssiReads = rs.size,
                ))
                if (r.success) repo.saveGattInfo(address, r.name, r.manufacturer, r.model, r.firmware)
                repo.recordEvent(if (r.success) "Info" else "Warning", "ConnectionTest", "Connect test on ...${address.takeLast(6)}: ${r.outcome} (${r.detail})")
            }
            _gattUi.value = GattUi.Done(address, r)
        }
    }

    fun dismissGatt() { _gattUi.value = GattUi.Idle }

    init {
        viewModelScope.launch {
            // A session left open by a previous run stays visible so it can be ended, never silently lost.
            if (engine.activeSession.value == null) repo.openSession()?.let { engine.setActiveSession(it.id, it.locationLabel) }
        }
    }

    fun updateSettings(block: (AppSettings) -> AppSettings) = settingsStore.update(block)

    fun startInspection(location: String, notes: String?, then: () -> Unit) {
        viewModelScope.launch {
            val s = repo.startSession(location, notes)
            engine.setActiveSession(s.id, s.locationLabel)
            settingsStore.update { it.copy(lastLocation = location) }
            then()
        }
    }

    fun endInspection(notes: String?, then: () -> Unit) {
        viewModelScope.launch {
            val a = engine.activeSession.value
            if (a != null) repo.endSession(a.first, notes)
            engine.setActiveSession(null, null)
            then()
        }
    }

    fun rename(address: String, name: String?) { viewModelScope.launch { repo.rename(address, name) } }

    suspend fun history(address: String, fromMs: Long) = repo.observations(address, fromMs)
    suspend fun previousAverage(address: String, beforeMs: Long) = repo.averageBefore(address, beforeMs)
}
