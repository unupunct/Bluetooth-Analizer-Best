package com.unupunct.fleetmonitor.domain

import java.security.MessageDigest

/** Android does not expose whether an address is public or random, so scans report Unknown and the server decides. */
enum class AddressKind { Unknown, Public, RandomStatic, RandomResolvablePrivate, RandomNonResolvablePrivate }

class ManufacturerBlock(val companyId: Int, val data: ByteArray)

/** One advertisement as heard by the phone. Times are wall-clock epoch milliseconds (UTC). */
class BleAdvertisement(
    val observationId: String,
    val observedAtMs: Long,
    val address: String,              // 12 hex digits, upper case, no separators
    val addressKind: AddressKind,
    val rssi: Int,
    val name: String?,
    val manufacturer: List<ManufacturerBlock>,
    val serviceUuids: List<String>,   // lower-case canonical form
    val payload: ByteArray,
    val txPower: Int? = null,
    val flags: Int? = null,
)

/**
 * Same device fingerprint as the Windows app (IdentityResolver.ComputeFingerprint) so the server can match
 * a phone's sighting with its own. Pinned by FingerprintTest against values produced by the C# code.
 */
object Fingerprint {
    private const val PREFIX_BYTES = 4

    fun compute(manufacturer: List<ManufacturerBlock>, serviceUuids: List<String>): String? {
        if (manufacturer.isEmpty() && serviceUuids.isEmpty()) return null
        val sb = StringBuilder()
        for (block in manufacturer.sortedBy { it.companyId }) {
            sb.append(String.format("%04X", block.companyId)).append(':')
            sb.append(hex(block.data, minOf(PREFIX_BYTES, block.data.size))).append(';')
        }
        for (u in serviceUuids.map { it.lowercase() }.sorted()) sb.append(u).append(';')
        val digest = MessageDigest.getInstance("SHA-256").digest(sb.toString().toByteArray(Charsets.UTF_8))
        return hex(digest, digest.size).substring(0, 32)
    }

    fun hex(bytes: ByteArray, count: Int = bytes.size): String {
        val out = StringBuilder(count * 2)
        for (i in 0 until count) out.append(String.format("%02X", bytes[i].toInt() and 0xFF))
        return out.toString()
    }
}

object Rssi {
    /** A relative label only. RSSI depends on antenna, orientation and obstruction; it is never a distance. */
    fun describe(rssi: Int): String = when {
        rssi >= -55 -> "Excellent"
        rssi >= -67 -> "Good"
        rssi >= -78 -> "Fair"
        rssi >= -88 -> "Weak"
        else -> "Very weak"
    }

    /** Needs 8+ samples. Compares the older and newer halves. */
    fun trend(samples: List<Int>): String {
        if (samples.size < 8) return "-"
        val half = samples.size / 2
        val d = samples.takeLast(half).average() - samples.take(half).average()
        return if (d >= 3) "Improving" else if (d <= -3) "Degrading" else "Stable"
    }

    fun stdDev(samples: List<Int>): Double {
        if (samples.size < 2) return 0.0
        val m = samples.average()
        return Math.sqrt(samples.sumOf { (it - m) * (it - m) } / (samples.size - 1))
    }

    /** Unstable = at least 10 samples and a spread of 6 dB or more. Never a verdict that the device is faulty. */
    fun isUnstable(samples: List<Int>): Boolean = samples.size >= 10 && stdDev(samples) >= 6.0
}
