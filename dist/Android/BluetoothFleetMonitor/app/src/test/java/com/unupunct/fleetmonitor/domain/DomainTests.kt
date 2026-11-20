package com.unupunct.fleetmonitor.domain

import org.junit.Assert.*
import org.junit.Test

private const val UUID_A = "0000fff0-0000-1000-8000-00805f9b34fb"
private const val UUID_B = "0000fff1-0000-1000-8000-00805f9b34fb"

private fun ad(address: String, t: Long, rssi: Int, name: String? = null) = BleAdvertisement(
    observationId = "id-$address-$t", observedAtMs = t, address = address, addressKind = AddressKind.Unknown, rssi = rssi, name = name,
    manufacturer = emptyList(), serviceUuids = emptyList(), payload = ByteArray(0),
)

class FingerprintTest {
    // These two expected values were produced by the Windows app's IdentityResolver (C#) and are pinned there too.
    @Test fun matches_the_windows_reference_with_service_uuids_in_any_order() {
        val blocks = listOf(ManufacturerBlock(0x004C, byteArrayOf(1, 2, 3, 4, 9, 9)))
        assertEquals("24407DB34235BDBF1BA91E61BA0EAF7E", Fingerprint.compute(blocks, listOf(UUID_B, UUID_A)))
        assertEquals("24407DB34235BDBF1BA91E61BA0EAF7E", Fingerprint.compute(blocks, listOf(UUID_A, UUID_B)))
    }

    @Test fun matches_the_windows_reference_for_manufacturer_data_only() {
        assertEquals("60804AF1DAFC1C460C2EAEC11C21DC6D", Fingerprint.compute(listOf(ManufacturerBlock(0x004C, byteArrayOf(1, 2, 3, 4))), emptyList()))
    }

    @Test fun ignores_volatile_bytes_after_the_prefix() {
        val a = Fingerprint.compute(listOf(ManufacturerBlock(0x004C, byteArrayOf(1, 2, 3, 4, 99, 100))), listOf(UUID_A))
        val b = Fingerprint.compute(listOf(ManufacturerBlock(0x004C, byteArrayOf(1, 2, 3, 4, 7, 8))), listOf(UUID_A))
        assertEquals(a, b)
    }

    @Test fun nothing_but_a_name_gives_no_fingerprint() {
        assertNull(Fingerprint.compute(emptyList(), emptyList()))
    }

    @Test fun uuid_case_does_not_matter() {
        assertEquals(Fingerprint.compute(emptyList(), listOf(UUID_A)), Fingerprint.compute(emptyList(), listOf(UUID_A.uppercase())))
    }
}

class AggregatorTest {
    @Test fun one_window_keeps_last_min_max_average_and_count() {
        val agg = ObservationAggregator(10_000)
        assertNull(agg.offer(ad("A1", 0, -60)))
        assertNull(agg.offer(ad("A1", 1_000, -80)))
        assertNull(agg.offer(ad("A1", 2_000, -70)))
        val done = agg.flushDue(10_000).single()
        assertEquals(3, done.advCount)
        assertEquals(-60, done.rssiMax); assertEquals(-80, done.rssiMin); assertEquals(-70, done.rssiLast)
        assertEquals(-70.0, done.rssiAvg, 0.001)
        assertEquals(0L, done.startMs); assertEquals(2_000L, done.endMs)   // original times preserved
    }

    @Test fun an_advertisement_after_the_window_closes_it_and_opens_the_next() {
        val agg = ObservationAggregator(10_000)
        agg.offer(ad("A1", 0, -60))
        val closed = agg.offer(ad("A1", 10_000, -90))
        assertNotNull(closed)
        assertEquals(-60, closed!!.rssiLast)
        val next = agg.flushAll().single()
        assertEquals(-90, next.rssiLast); assertEquals(10_000L, next.startMs)
    }

    @Test fun nothing_is_lost_a_silent_devices_last_window_is_flushed() {
        val agg = ObservationAggregator(10_000)
        agg.offer(ad("A1", 0, -60))
        assertTrue(agg.flushDue(5_000).isEmpty())          // window still open
        assertEquals(1, agg.flushDue(10_000).size)          // time passed: written even though the device went quiet
        assertTrue(agg.flushAll().isEmpty())
    }

    @Test fun a_zero_window_stores_every_advertisement() {
        val agg = ObservationAggregator(0)
        assertNotNull(agg.offer(ad("A1", 0, -60)))
        assertNotNull(agg.offer(ad("A1", 1, -61)))
        assertTrue(agg.flushAll().isEmpty())
    }

    @Test fun addresses_are_independent() {
        val agg = ObservationAggregator(10_000)
        agg.offer(ad("A1", 0, -60)); agg.offer(ad("B2", 100, -50))
        val all = agg.flushAll().associateBy { it.address }
        assertEquals(-60, all.getValue("A1").rssiLast); assertEquals(-50, all.getValue("B2").rssiLast)
    }

    @Test fun the_latest_non_blank_name_wins_and_ids_are_unique() {
        val agg = ObservationAggregator(10_000)
        agg.offer(ad("A1", 0, -60)); agg.offer(ad("A1", 100, -60, name = "LVS-Lush"))
        val a = agg.flushAll().single()
        assertEquals("LVS-Lush", a.name)
        val agg2 = ObservationAggregator(0)
        assertNotEquals(agg2.offer(ad("A1", 0, -60))!!.id, agg2.offer(ad("A1", 1, -60))!!.id)
    }
}

class PresenceStatusTest {
    private val now = 1_000_000L
    private val window = 120_000L
    @Test fun heard_within_the_window_is_detected() =
        assertEquals(DevicePresence.Detected, PresenceStatus.evaluate(now - 30_000, now, window, true, emptyList()))
    @Test fun silent_with_a_healthy_scanner_is_not_heard_recently() =
        assertEquals(DevicePresence.Missing, PresenceStatus.evaluate(now - 600_000, now, window, true, emptyList()))
    @Test fun silent_while_the_scanner_was_not_running_is_uncertain() =
        assertEquals(DevicePresence.Uncertain, PresenceStatus.evaluate(now - 600_000, now, window, false, emptyList()))
    @Test fun silence_overlapping_a_coverage_gap_is_uncertain() =
        assertEquals(DevicePresence.Uncertain, PresenceStatus.evaluate(now - 600_000, now, window, true, listOf(CoverageGap(now - 500_000, now - 300_000, "bt off"))))
    @Test fun a_gap_that_ended_before_the_last_sighting_does_not_matter() =
        assertEquals(DevicePresence.Missing, PresenceStatus.evaluate(now - 600_000, now, window, true, listOf(CoverageGap(now - 9_000_000, now - 8_000_000, "old"))))
    @Test fun an_open_gap_makes_it_uncertain() =
        assertEquals(DevicePresence.Uncertain, PresenceStatus.evaluate(now - 600_000, now, window, true, listOf(CoverageGap(now - 1_000, null, "bt off"))))
    @Test fun never_heard_is_uncertain() =
        assertEquals(DevicePresence.Uncertain, PresenceStatus.evaluate(null, now, window, true, emptyList()))
}

class ScanHealthTest {
    private var t = 1_000_000L
    private fun health() = ScanHealth(10_000) { t }

    @Test fun running_with_a_recent_advertisement_is_running() {
        val h = health(); h.started(); t += 2_000; h.advertisementReceived(); t += 3_000
        assertEquals(ScannerState.Running, h.current().state)
    }

    @Test fun running_but_hearing_nothing_says_so() {
        val h = health(); h.started(); t += 11_000
        val s = h.current()
        assertEquals(ScannerState.RunningNoAdvertisements, s.state)
        assertTrue(s.message.contains("not proof"))
    }

    @Test fun advertisements_resuming_clear_the_warning() {
        val h = health(); h.started(); t += 30_000
        assertEquals(ScannerState.RunningNoAdvertisements, h.current().state)
        h.advertisementReceived()
        assertEquals(ScannerState.Running, h.current().state)
    }

    @Test fun unexpected_stop_opens_a_gap_that_closes_on_restart() {
        val h = health(); h.started(); t += 5_000
        val stopAt = t
        h.stopped(requestedByUser = false, reason = "Bluetooth was turned off")
        assertEquals(ScannerState.Interrupted, h.current().state)
        assertEquals(stopAt, h.gaps.single().startMs); assertNull(h.gaps.single().endMs)
        t += 20_000; h.started()
        assertEquals(t, h.gaps.single().endMs)
        assertEquals(ScannerState.Running, h.current().state)
    }

    @Test fun a_user_stop_is_not_a_gap() {
        val h = health(); h.started(); h.stopped(requestedByUser = true, reason = null)
        assertEquals(ScannerState.Stopped, h.current().state)
        assertTrue(h.gaps.isEmpty())
    }

    @Test fun a_gap_found_after_a_restart_is_recorded_as_already_closed() {
        val h = health(); h.recordPastGap(100, 200, "process stopped")
        assertEquals(CoverageGap(100, 200, "process stopped"), h.gaps.single())
    }
}

class RssiTest {
    @Test fun labels() {
        assertEquals("Excellent", Rssi.describe(-50)); assertEquals("Good", Rssi.describe(-60)); assertEquals("Fair", Rssi.describe(-70))
        assertEquals("Weak", Rssi.describe(-85)); assertEquals("Very weak", Rssi.describe(-95))
    }
    @Test fun trend_needs_enough_samples() = assertEquals("-", Rssi.trend(listOf(-60, -70, -80)))
    @Test fun trend_compares_halves() {
        assertEquals("Degrading", Rssi.trend(List(5) { -60 } + List(5) { -75 }))
        assertEquals("Improving", Rssi.trend(List(5) { -75 } + List(5) { -60 }))
        assertEquals("Stable", Rssi.trend(List(10) { -65 }))
    }
    @Test fun unstable_needs_ten_samples_and_a_wide_spread() {
        assertFalse(Rssi.isUnstable(List(9) { if (it % 2 == 0) -50 else -90 }))
        assertTrue(Rssi.isUnstable(List(10) { if (it % 2 == 0) -50 else -90 }))
        assertFalse(Rssi.isUnstable(List(10) { -65 }))
    }
}
