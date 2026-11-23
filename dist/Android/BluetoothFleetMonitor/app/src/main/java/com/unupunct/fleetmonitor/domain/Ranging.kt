package com.unupunct.fleetmonitor.domain

import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow

enum class RangeBasis(val label: String) {
    Calibrated("calibrated for this device"),
    TxPower("from the device's advertised TX power"),
    Default("generic default, not calibrated"),
}

/** A distance estimate that carries its own error bars. [lowMeters]..[highMeters] is roughly a 1-sigma band. */
class RangeEstimate(
    val meters: Double,
    val lowMeters: Double,
    val highMeters: Double,
    val basis: RangeBasis,
    val smoothedRssi: Double,
    val sigmaDb: Double,
    /** True for a first-reading estimate (fewer than 3 samples): shown straight away, firmed up within seconds. */
    val provisional: Boolean = false,
)

/** One-dimensional Kalman filter for RSSI: follows a walking user (a few dB per second) but averages out the noise. */
class RssiKalman(private val processVarPerSec: Double = 0.4, private val measurementVar: Double = 25.0) {
    private var x: Double? = null
    private var p = 25.0

    fun update(z: Double, dtSec: Double = 1.0): Double {
        val prev = x
        if (prev == null) { x = z; p = measurementVar; return z }
        p += processVarPerSec * dtSec
        val k = p / (p + measurementVar)
        val next = prev + k * (z - prev)
        p *= (1 - k)
        x = next
        return next
    }
}

/**
 * Log-distance path-loss ranging: d = 10 ^ ((RSSI@1m - RSSI) / (10 n)).
 * This is an estimate, not a measurement. Typical indoor error is a factor of about 1.5 to 2 either way, which is why every
 * estimate comes with a band. Calibrating a device at a known distance is the single biggest accuracy gain.
 */
object Ranging {
    const val DEFAULT_RSSI_AT_1M = -59.0
    /** Phones typically see RSSI at 1 m of roughly (TX power - 59 dB) because of antenna and body losses. */
    const val TX_POWER_TO_RSSI_1M = 59.0
    const val DEFAULT_PATH_LOSS_EXPONENT = 2.5
    private const val SIGMA_CALIBRATED = 4.0
    private const val SIGMA_TX_POWER = 6.0
    private const val SIGMA_DEFAULT = 8.0

    fun reference(calibratedRssi1m: Int?, txPower: Int?): Pair<Double, RangeBasis> = when {
        calibratedRssi1m != null -> calibratedRssi1m.toDouble() to RangeBasis.Calibrated
        txPower != null -> (txPower - TX_POWER_TO_RSSI_1M) to RangeBasis.TxPower
        else -> DEFAULT_RSSI_AT_1M to RangeBasis.Default
    }

    fun distanceMeters(rssi: Double, rssiAt1m: Double, n: Double): Double = 10.0.pow((rssiAt1m - rssi) / (10.0 * n))

    /** The RSSI-at-1-m that makes [rssi] read as [meters]: what "I am this far from the device" calibrates to. */
    fun referenceFromKnownDistance(rssi: Double, meters: Double, n: Double): Double = rssi + 10.0 * n * log10(max(meters, 0.1))

    fun smooth(samples: List<Int>): Double? {
        if (samples.isEmpty()) return null
        val k = RssiKalman()
        var v = 0.0
        for (s in samples) v = k.update(s.toDouble())
        return v
    }

    /** A first guess from a single reading so a newly heard device can appear on the radar at once. Wide error bars, flagged provisional. */
    fun provisional(rssi: Double, calibratedRssi1m: Int?, txPower: Int?, n: Double): RangeEstimate {
        val (ref, basis) = reference(calibratedRssi1m, txPower)
        val sigma = 8.0
        fun d(r: Double) = min(500.0, max(0.1, distanceMeters(r, ref, n)))
        return RangeEstimate(d(rssi), d(rssi + sigma), d(rssi - sigma), basis, rssi, sigma, provisional = true)
    }

    /** Needs at least 3 one-per-second samples for a proper estimate; fewer are too noisy (see [provisional]). */
    fun estimate(samples: List<Int>, calibratedRssi1m: Int?, txPower: Int?, n: Double): RangeEstimate? {
        if (samples.size < 3) return null
        val smoothed = smooth(samples) ?: return null
        val (ref, basis) = reference(calibratedRssi1m, txPower)
        val baseline = when (basis) { RangeBasis.Calibrated -> SIGMA_CALIBRATED; RangeBasis.TxPower -> SIGMA_TX_POWER; RangeBasis.Default -> SIGMA_DEFAULT }
        val recent = samples.takeLast(10)
        val sigma = max(baseline, Rssi.stdDev(recent))
        fun d(r: Double) = min(500.0, max(0.1, distanceMeters(r, ref, n)))
        return RangeEstimate(d(smoothed), d(smoothed + sigma), d(smoothed - sigma), basis, smoothed, sigma)
    }

    fun format(meters: Double): String = when {
        meters < 1 -> "%.1f m".format(meters)
        meters < 10 -> "%.1f m".format(meters)
        else -> "%.0f m".format(meters)
    }
}
