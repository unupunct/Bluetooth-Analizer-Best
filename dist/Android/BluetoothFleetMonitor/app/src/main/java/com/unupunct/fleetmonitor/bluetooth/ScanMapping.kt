package com.unupunct.fleetmonitor.bluetooth

import android.bluetooth.le.ScanResult
import android.os.SystemClock
import com.unupunct.fleetmonitor.domain.AddressKind
import com.unupunct.fleetmonitor.domain.BleAdvertisement
import com.unupunct.fleetmonitor.domain.ManufacturerBlock
import java.util.UUID

object CompanyIds {
    // Same short list as the Windows app. Unknown ids are shown as hex, never guessed.
    private val names = mapOf(
        0x0002 to "Intel", 0x0006 to "Microsoft", 0x000D to "Texas Instruments", 0x000F to "Broadcom", 0x0046 to "MediaTek",
        0x004C to "Apple", 0x0059 to "Nordic Semiconductor", 0x0075 to "Samsung", 0x0087 to "Garmin", 0x00E0 to "Google",
        0x0171 to "Amazon", 0x01DA to "Logitech",
    )
    fun describe(id: Int): String = names[id] ?: String.format("Company 0x%04X", id)
}

object ScanMapping {
    /** RSSI 127 is the platform's "not available" marker; it is not a measurement. */
    private const val RSSI_UNAVAILABLE = 127

    /**
     * ScanResult.timestampNanos is time since boot. Converting it to wall-clock time matters because batched
     * and PendingIntent results can arrive well after the advertisement was heard.
     */
    fun toAdvertisement(r: ScanResult, nowMs: Long = System.currentTimeMillis()): BleAdvertisement? {
        if (r.rssi == RSSI_UNAVAILABLE) return null
        val ageMs = (SystemClock.elapsedRealtimeNanos() - r.timestampNanos) / 1_000_000
        val observedMs = nowMs - ageMs.coerceAtLeast(0)
        val record = r.scanRecord
        val manufacturer = ArrayList<ManufacturerBlock>()
        record?.manufacturerSpecificData?.let { sparse ->
            for (i in 0 until sparse.size()) manufacturer.add(ManufacturerBlock(sparse.keyAt(i), sparse.valueAt(i) ?: ByteArray(0)))
        }
        val uuids = record?.serviceUuids?.map { it.uuid.toString().lowercase() } ?: emptyList()
        val tx = record?.txPowerLevel?.takeIf { it != Int.MIN_VALUE }
        val flags = record?.advertiseFlags?.takeIf { it >= 0 }
        return BleAdvertisement(
            observationId = UUID.randomUUID().toString(),
            observedAtMs = observedMs,
            address = r.device.address.replace(":", "").uppercase(),
            addressKind = AddressKind.Unknown,
            rssi = r.rssi,
            name = record?.deviceName?.takeIf { it.isNotBlank() },
            manufacturer = manufacturer,
            serviceUuids = uuids,
            payload = record?.bytes ?: ByteArray(0),
            txPower = tx,
            flags = flags,
        )
    }

    fun scanFailureText(code: Int): String = when (code) {
        1 -> "a scan with the same settings is already running"
        2 -> "the app could not register the scan with the Bluetooth stack"
        3 -> "internal Bluetooth error"
        4 -> "this phone does not support this kind of scan"
        5 -> "the Bluetooth chip is out of scan resources"
        6 -> "Android is limiting how often scans may start (5 starts in 30 seconds)"
        else -> "scan failed with error code $code"
    }
}
