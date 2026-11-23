package com.unupunct.fleetmonitor.data

import androidx.room.withTransaction
import com.unupunct.fleetmonitor.domain.AggregatedObservation
import com.unupunct.fleetmonitor.domain.CoverageGap
import com.unupunct.fleetmonitor.domain.Fingerprint
import kotlinx.coroutines.flow.Flow
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** Extra facts about a device that come from the advertisement itself rather than from RSSI windows. */
class DeviceFacts(var manufacturer: String?, var fingerprint: String?, val addressKind: String) {
    val companyIds = LinkedHashSet<Int>()
    val services16 = LinkedHashSet<Int>()
    var appearance: Int? = null
}

private fun parseInts(s: String?, radix: Int): List<Int> = s?.split(',')?.mapNotNull { it.trim().takeIf(String::isNotEmpty)?.toIntOrNull(radix) } ?: emptyList()
private fun joinInts(v: Collection<Int>, radix: Int): String? = v.toSortedSet().joinToString(",") { it.toString(radix).uppercase() }.ifEmpty { null }

@Singleton
class FleetRepository @Inject constructor(private val db: FleetDatabase) {
    val devices: Flow<List<DeviceEntity>> = db.devices().observeAll()
    val observationCount: Flow<Long> = db.observations().observeCount()
    val pendingUploadCount: Flow<Long> = db.observations().observePending()
    val sessions: Flow<List<SessionEntity>> = db.sessions().observeAll()
    val sessionSummaries: Flow<List<SessionSummary>> = db.observations().observeSessionSummaries()
    val gaps: Flow<List<CoverageGapEntity>> = db.diagnostics().observeGaps()
    val events: Flow<List<AppEventEntity>> = db.diagnostics().observeEvents()

    suspend fun allDevices() = db.devices().getAll()
    suspend fun device(address: String) = db.devices().get(address)
    suspend fun observations(address: String, fromMs: Long) = db.observations().forDevice(address, fromMs)
    suspend fun addressesInSession(id: String) = db.observations().addressesInSession(id)
    suspend fun averageBefore(address: String, beforeMs: Long) = db.observations().averageBefore(address, beforeMs)
    suspend fun allGaps(): List<CoverageGap> = db.diagnostics().allGaps().map { CoverageGap(it.startMs, it.endMs, it.reason) }
    suspend fun rename(address: String, name: String?) = db.devices().rename(address, name?.trim()?.ifBlank { null })
    suspend fun setCalibration(address: String, ref: Int?) = db.devices().setCalibration(address, ref)
    suspend fun saveGattInfo(address: String, name: String?, maker: String?, model: String?, fw: String?) =
        db.devices().setGattInfo(address, name, maker, model, fw, System.currentTimeMillis())
    suspend fun clearUploadMarks() = db.observations().clearUploadMarks()
    suspend fun pendingUploads(): Long = db.observations().pendingCount()
    suspend fun pendingBatch(limit: Int) = db.observations().pending(limit)
    suspend fun markUploaded(ids: List<String>, ts: Long) = db.observations().markUploaded(ids, ts)
    suspend fun allSessions() = db.sessions().all()
    suspend fun recentConnectionTests() = db.connectionTests().recent()
    suspend fun logConnectionTest(t: ConnectionTestEntity) = db.connectionTests().insert(t)
    fun connectionTests(address: String): Flow<List<ConnectionTestEntity>> = db.connectionTests().observeFor(address)

    /**
     * Writes a batch atomically: observation rows (duplicates by id ignored) and the matching device statistics.
     * Device "current" values only move forward in time, so a late batch cannot make a device look freshly seen.
     */
    suspend fun saveBatch(
        rows: List<AggregatedObservation>,
        facts: Map<String, DeviceFacts>,
        sessionId: String?,
        location: String?,
        mode: ScanMode,
    ): Int {
        if (rows.isEmpty()) return 0
        return db.withTransaction {
            val entities = rows.map {
                ObservationEntity(
                    id = it.id, address = it.address, addressKind = it.addressKind.name,
                    timestampMs = it.startMs, windowEndMs = it.endMs,
                    rssi = it.rssiLast, rssiMin = it.rssiMin, rssiMax = it.rssiMax, rssiAvg = it.rssiAvg, advCount = it.advCount,
                    name = it.name, manufacturerData = it.manufacturerData, serviceUuids = it.serviceUuids,
                    advertisementData = it.advertisementData, sessionId = sessionId, locationLabel = location, scanMode = mode.name,
                    txPower = it.txPower, advertiseFlags = it.flags,
                )
            }
            val results = db.observations().insertAll(entities)
            val inserted = entities.filterIndexed { i, _ -> results[i] != -1L }
            if (inserted.isEmpty()) return@withTransaction 0

            val known = db.devices().getAll().associateBy { it.address }.toMutableMap()
            for (o in inserted) {
                val f = facts[o.address]
                val d = known[o.address]
                val updated = if (d == null) {
                    DeviceEntity(
                        address = o.address, advertisedName = o.name, customName = null,
                        manufacturer = f?.manufacturer, fingerprint = f?.fingerprint, addressKind = f?.addressKind ?: o.addressKind,
                        firstSeenMs = o.timestampMs, lastSeenMs = o.windowEndMs, lastRssi = o.rssi,
                        minRssi = o.rssiMin, maxRssi = o.rssiMax, rssiSum = Math.round(o.rssiAvg), observationCount = 1,
                        advertisementCount = o.advCount.toLong(),
                        companyIds = joinInts(f?.companyIds ?: emptySet(), 10),
                        serviceUuids = joinInts(f?.services16 ?: emptySet(), 16),
                        appearance = f?.appearance,
                    )
                } else {
                    val newest = o.windowEndMs >= d.lastSeenMs
                    d.copy(
                        advertisedName = if (newest && !o.name.isNullOrBlank()) o.name else d.advertisedName,
                        manufacturer = d.manufacturer ?: f?.manufacturer,
                        fingerprint = d.fingerprint ?: f?.fingerprint,
                        firstSeenMs = minOf(d.firstSeenMs, o.timestampMs),
                        lastSeenMs = maxOf(d.lastSeenMs, o.windowEndMs),
                        lastRssi = if (newest) o.rssi else d.lastRssi,
                        minRssi = minOf(d.minRssi, o.rssiMin), maxRssi = maxOf(d.maxRssi, o.rssiMax),
                        rssiSum = d.rssiSum + Math.round(o.rssiAvg),
                        observationCount = d.observationCount + 1,
                        advertisementCount = d.advertisementCount + o.advCount,
                        companyIds = joinInts(parseInts(d.companyIds, 10) + (f?.companyIds ?: emptySet()), 10),
                        serviceUuids = joinInts(parseInts(d.serviceUuids, 16) + (f?.services16 ?: emptySet()), 16),
                        appearance = d.appearance ?: f?.appearance,
                    )
                }
                known[o.address] = updated
            }
            db.devices().upsert(inserted.map { known.getValue(it.address) }.distinctBy { it.address })
            inserted.size
        }
    }

    // ---- sessions ----
    suspend fun startSession(location: String, notes: String?): SessionEntity {
        val s = SessionEntity(UUID.randomUUID().toString(), location, System.currentTimeMillis(), null, notes?.ifBlank { null })
        db.sessions().upsert(s)
        return s
    }

    suspend fun endSession(id: String, notes: String?) {
        val s = db.sessions().get(id) ?: return
        db.sessions().upsert(s.copy(endedAtMs = System.currentTimeMillis(), notes = notes?.ifBlank { null } ?: s.notes))
    }

    suspend fun openSession(): SessionEntity? = db.sessions().open()

    // ---- diagnostics ----
    suspend fun recordEvent(severity: String, type: String, message: String) {
        db.diagnostics().insertEvent(AppEventEntity(timestampMs = System.currentTimeMillis(), severity = severity, type = type, message = message))
    }

    suspend fun openGap(startMs: Long, reason: String) { db.diagnostics().insertGap(CoverageGapEntity(startMs = startMs, endMs = null, reason = reason)) }
    suspend fun closeGaps(endMs: Long) { db.diagnostics().closeOpenGaps(endMs) }
    suspend fun addPastGap(startMs: Long, endMs: Long, reason: String) { db.diagnostics().insertGap(CoverageGapEntity(startMs = startMs, endMs = endMs, reason = reason)) }
}

fun fingerprintOf(manufacturer: List<com.unupunct.fleetmonitor.domain.ManufacturerBlock>, uuids: List<String>) = Fingerprint.compute(manufacturer, uuids)
