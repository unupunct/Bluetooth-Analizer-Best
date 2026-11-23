package com.unupunct.fleetmonitor.ui

import com.unupunct.fleetmonitor.domain.DeviceClassifier
import com.unupunct.fleetmonitor.domain.DevicePresence
import com.unupunct.fleetmonitor.domain.Ranging
import com.unupunct.fleetmonitor.domain.Rssi
import kotlin.math.sin

/**
 * Fictional devices for photographing the app without showing anyone's real surroundings. Only used while Demo data is switched on
 * in Settings, and a "DEMO DATA" banner is shown on every screen while it is. Nothing here is ever stored or uploaded.
 */
object DemoData {
    private class Spec(
        val name: String, val maker: String, val company: Int, val services: Set<Int> = emptySet(), val appearance: Int? = null,
        val base: Int, val tx: Int? = null, val wobble: Double = 3.0, val seed: Int,
    )

    private val specs = listOf(
        Spec("Keyboard 02", "Logitech", 0x01DA, setOf(0x1812), 0x03C1, -47, seed = 1),
        Spec("Mouse 01", "Logitech", 0x01DA, setOf(0x1812), 0x03C2, -52, seed = 2),
        Spec("Studio Light 01", "Nordic Semiconductor", 0x0059, base = -55, seed = 3),
        Spec("Studio Light 02", "Nordic Semiconductor", 0x0059, base = -60, seed = 4),
        Spec("Studio Light 03", "Nordic Semiconductor", 0x0059, base = -71, wobble = 7.0, seed = 5),
        Spec("ILCE-DEMO", "Sony", 0x012D, base = -58, seed = 6),
        Spec("Speaker 01", "Bose", 0x009E, base = -66, seed = 7),
        Spec("Phone 02", "Apple", 0x004C, base = -62, tx = 8, seed = 8),
        Spec("Phone 04", "Samsung", 0x0075, base = -76, seed = 9),
        Spec("Watch 01", "Garmin", 0x0087, base = -64, seed = 10),
        Spec("Tag 05", "Samsung", 0x0075, setOf(0xFD5A), null, -69, seed = 11),
        Spec("Desk Sensor 01", "Microsoft", 0x0006, setOf(0x181A), null, -72, seed = 12),
        Spec("Desk Sensor 02", "Microsoft", 0x0006, setOf(0x181A), null, -81, seed = 13),
        Spec("Studio Light 04", "Nordic Semiconductor", 0x0059, base = -84, seed = 14),
        Spec("Keyboard 05", "Logitech", 0x01DA, setOf(0x1812), 0x03C1, -88, wobble = 6.0, seed = 15),
        Spec("Tag 02", "Samsung", 0x0075, setOf(0xFD5A), null, -78, seed = 16),
        Spec("Mouse 04", "Logitech", 0x01DA, setOf(0x1812), 0x03C2, -74, seed = 17),
        Spec("Speaker 02", "Bose", 0x009E, base = -91, seed = 18),
    )

    fun rows(nowMs: Long, pathLoss: Double): List<DeviceRow> = specs.map { s ->
        val t = nowMs / 1000.0
        val base = (-64 + (s.base + 47) * 0.62).toInt() // spread the fictional devices over the radar instead of piling them at the centre
        // a fixed pseudo-random wobble per device and second, plus a slow swell, so the list and the radar look alive but never random-walk away
        val samples = List(30) { i ->
            val k = (t - (29 - i)).toLong()
            val jitter = (((k * 31 + s.seed * 17) % 7) - 3) / 3.0
            (base + s.wobble * sin((k + s.seed * 5) / 9.0) * 0.6 + s.wobble * jitter * 0.5).toInt()
        }
        val cls = DeviceClassifier.classify(s.name, listOf(s.company), s.services, s.appearance)
        val range = Ranging.estimate(samples, null, s.tx, pathLoss)
        DeviceRow(
            address = "D0DE%08X".format(s.seed), name = s.name, manufacturer = s.maker, rssi = samples.last(),
            quality = Rssi.describe(samples.last()), trend = Rssi.trend(samples), unstable = Rssi.isUnstable(samples),
            lastSeenMs = nowMs, presence = DevicePresence.Detected, heardThisRun = true, samples = samples,
            observations = 300L + s.seed * 17, advertisementHex = null, firstSeenMs = nowMs - 3_600_000,
            classification = cls, range = range, connectable = true, txPower = s.tx,
        )
    }
}
