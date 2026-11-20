package com.unupunct.fleetmonitor.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.unupunct.fleetmonitor.bluetooth.ScanEngine
import com.unupunct.fleetmonitor.data.*
import com.unupunct.fleetmonitor.domain.*
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import javax.inject.Inject

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

@HiltViewModel
class FleetViewModel @Inject constructor(
    val engine: ScanEngine,
    val repo: FleetRepository,
    private val settingsStore: SettingsStore,
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

    val rows: StateFlow<List<DeviceRow>> = combine(repo.devices, engine.live, settingsStore.settings, engine.status, ticker) { db, live, s, st, now ->
        val liveBy = live.associateBy { it.address }
        val persistedGaps = gaps.value.map { CoverageGap(it.startMs, it.endMs, it.reason) }
        val allGaps = (persistedGaps + engine.coverageGaps()).distinct()
        val window = s.presenceWindowSec * 1000L
        val running = st.state == ScannerState.Running
        val fromDb = db.map { d ->
            val l = liveBy[d.address]
            val last = maxOf(d.lastSeenMs, l?.lastSeenMs ?: 0L)
            val rssi = if (l != null && l.lastSeenMs >= d.lastSeenMs) l.rssi else d.lastRssi
            val samples = l?.samples ?: emptyList()
            DeviceRow(
                address = d.address, name = l?.name?.takeIf { it.isNotBlank() }?.let { n -> d.customName ?: n } ?: d.displayName,
                manufacturer = d.manufacturer ?: l?.manufacturer, rssi = rssi, quality = Rssi.describe(rssi),
                trend = Rssi.trend(samples), unstable = Rssi.isUnstable(samples), lastSeenMs = last,
                presence = PresenceStatus.evaluate(last, now, window, running, allGaps), heardThisRun = l != null, samples = samples,
                observations = d.observationCount, advertisementHex = l?.advertisementHex, firstSeenMs = d.firstSeenMs,
            )
        }
        val dbAddresses = db.map { it.address }.toSet()
        val onlyLive = live.filter { it.address !in dbAddresses }.map { l ->
            DeviceRow(
                address = l.address, name = l.name ?: "Device ${l.address.takeLast(6)}", manufacturer = l.manufacturer, rssi = l.rssi,
                quality = Rssi.describe(l.rssi), trend = Rssi.trend(l.samples), unstable = Rssi.isUnstable(l.samples), lastSeenMs = l.lastSeenMs,
                presence = PresenceStatus.evaluate(l.lastSeenMs, now, window, running, allGaps), heardThisRun = true, samples = l.samples,
                observations = 0, advertisementHex = l.advertisementHex, firstSeenMs = l.lastSeenMs,
            )
        }
        fromDb + onlyLive
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), emptyList())

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
