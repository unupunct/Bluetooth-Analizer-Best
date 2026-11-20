package com.unupunct.fleetmonitor.domain

import java.util.UUID

/** Everything heard from one address during one sampling window, summarised without losing the extremes or the count. */
class AggregatedObservation(
    val id: String,
    val address: String,
    val addressKind: AddressKind,
    val startMs: Long,
    val endMs: Long,
    val rssiLast: Int,
    val rssiMin: Int,
    val rssiMax: Int,
    val rssiAvg: Double,
    val advCount: Int,
    val name: String?,
    val manufacturerData: String?,
    val serviceUuids: String?,
    val advertisementData: String?,
)

/**
 * A phone hears hundreds of advertisements a second; storing each one would fill it in hours. Instead each
 * address gets one row per window carrying last/min/max/average RSSI and the number of advertisements heard,
 * so the loss of detail is explicit and nothing is silently discarded. A window of 0 ms stores every advertisement.
 */
class ObservationAggregator(private val windowMs: Long) {
    private class Bucket(val first: BleAdvertisement) {
        var last: BleAdvertisement = first
        var min = first.rssi
        var max = first.rssi
        var sum = first.rssi.toLong()
        var count = 1
        var name: String? = first.name
        fun add(ad: BleAdvertisement) {
            last = ad; if (ad.rssi < min) min = ad.rssi; if (ad.rssi > max) max = ad.rssi
            sum += ad.rssi; count++
            if (!ad.name.isNullOrBlank()) name = ad.name
        }
    }

    private val open = HashMap<String, Bucket>()

    /** Returns the finished window for this address when the new advertisement falls outside it. */
    fun offer(ad: BleAdvertisement): AggregatedObservation? {
        val b = open[ad.address]
        if (b == null) { open[ad.address] = Bucket(ad); return if (windowMs <= 0) close(ad.address) else null }
        if (ad.observedAtMs - b.first.observedAtMs >= windowMs) {
            val done = close(ad.address)
            open[ad.address] = Bucket(ad)
            return if (windowMs <= 0) close(ad.address) else done
        }
        b.add(ad)
        return null
    }

    /** Closes windows whose time has passed without a new advertisement (so a silent device's last window is still written). */
    fun flushDue(nowMs: Long): List<AggregatedObservation> {
        val due = open.filterValues { nowMs - it.first.observedAtMs >= windowMs }.keys.toList()
        return due.mapNotNull { close(it) }
    }

    fun flushAll(): List<AggregatedObservation> = open.keys.toList().mapNotNull { close(it) }

    private fun close(address: String): AggregatedObservation? {
        val b = open.remove(address) ?: return null
        val ad = b.first
        return AggregatedObservation(
            id = UUID.randomUUID().toString(),
            address = address,
            addressKind = ad.addressKind,
            startMs = ad.observedAtMs,
            endMs = b.last.observedAtMs,
            rssiLast = b.last.rssi, rssiMin = b.min, rssiMax = b.max,
            rssiAvg = b.sum.toDouble() / b.count,
            advCount = b.count,
            name = b.name,
            manufacturerData = if (ad.manufacturer.isEmpty()) null
                else ad.manufacturer.joinToString(";") { String.format("%04X:%s", it.companyId, Fingerprint.hex(it.data)) },
            serviceUuids = if (ad.serviceUuids.isEmpty()) null else ad.serviceUuids.joinToString(";"),
            advertisementData = if (ad.payload.isEmpty()) null else Fingerprint.hex(ad.payload),
        )
    }
}
