package com.unupunct.fleetmonitor.domain

import org.junit.Assert.*
import org.junit.Test
import java.util.Random
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.sin

class RangingTest {
    @Test fun at_the_reference_level_the_distance_is_one_metre() =
        assertEquals(1.0, Ranging.distanceMeters(-59.0, -59.0, 2.0), 1e-9)

    @Test fun twenty_db_weaker_is_ten_metres_in_free_space() =
        assertEquals(10.0, Ranging.distanceMeters(-79.0, -59.0, 2.0), 1e-9)

    @Test fun indoor_exponent_makes_the_same_drop_mean_a_shorter_distance() =
        assertEquals(6.31, Ranging.distanceMeters(-79.0, -59.0, 2.5), 0.01)

    @Test fun calibrating_at_a_known_distance_reproduces_that_distance() {
        val ref = Ranging.referenceFromKnownDistance(-56.0, 2.0, 2.5)
        assertEquals(2.0, Ranging.distanceMeters(-56.0, ref, 2.5), 1e-9)
    }

    @Test fun reference_prefers_calibration_then_tx_power_then_default() {
        assertEquals(RangeBasis.Calibrated, Ranging.reference(-50, -4).second)
        assertEquals(RangeBasis.TxPower, Ranging.reference(null, -4).second)
        assertEquals(-63.0, Ranging.reference(null, -4).first, 1e-9)
        assertEquals(RangeBasis.Default, Ranging.reference(null, null).second)
    }

    @Test fun too_few_samples_give_no_estimate() = assertNull(Ranging.estimate(listOf(-60, -61), null, null, 2.5))

    @Test fun the_band_always_brackets_the_estimate_and_calibration_tightens_it() {
        val samples = List(20) { -70 }
        val generic = Ranging.estimate(samples, null, null, 2.5)!!
        val calibrated = Ranging.estimate(samples, -59, null, 2.5)!!
        assertTrue(generic.lowMeters < generic.meters && generic.meters < generic.highMeters)
        assertTrue(calibrated.lowMeters < calibrated.meters && calibrated.meters < calibrated.highMeters)
        assertTrue((calibrated.highMeters / calibrated.lowMeters) < (generic.highMeters / generic.lowMeters))
    }

    @Test fun smoothing_removes_most_of_the_noise_without_bias() {
        val finals = (1L..30L).map { seed ->
            val rnd = Random(seed)
            Ranging.smooth(List(40) { Math.round(-70 + rnd.nextGaussian() * 4).toInt() })!!
        }
        val mean = finals.average()
        val sd = Rssi.stdDev(finals.map { it.toInt() * 10 }) / 10.0        // spread of the smoothed result across runs
        println("smoothing: mean %.2f dB (true -70), spread %.2f dB (raw noise 4.0)".format(mean, sd))
        assertEquals(-70.0, mean, 1.0)
        assertTrue("spread $sd", sd < 2.4)
    }

    @Test fun smoothing_follows_a_signal_that_really_changes() {
        val samples = List(15) { -80 } + List(15) { -60 }       // walked up to the device
        assertEquals(-60.0, Ranging.smooth(samples)!!, 3.0)
    }
}

class AdParserTest {
    private fun bytes(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }

    @Test fun parses_appearance_services_and_name() {
        val payload = bytes(2, 0x01, 0x06,  3, 0x19, 0xC1, 0x03,  3, 0x03, 0x12, 0x18,  5, 0x09, 'T'.code, 'e'.code, 's'.code, 't'.code)
        assertEquals(0x03C1, AdParser.appearance(payload))
        assertEquals(setOf(0x1812), AdParser.services16(payload))
        assertEquals("Test", AdParser.localName(payload))
    }

    @Test fun a_truncated_payload_never_throws() {
        val payload = bytes(2, 0x01, 0x06,  9, 0x09, 'T'.code)    // second structure claims 9 bytes but only 2 remain
        assertEquals(1, AdParser.parse(payload).size)
        assertNull(AdParser.appearance(payload))
    }

    @Test fun service_data_counts_as_a_service() =
        assertEquals(setOf(0xFEAA), AdParser.services16(bytes(5, 0x16, 0xAA, 0xFE, 0x00, 0x01)))

    @Test fun hex_round_trip() = assertArrayEquals(bytes(0x02, 0x01, 0x06), AdParser.hexToBytes("020106"))
}

class DeviceClassifierTest {
    @Test fun appearance_identifies_a_keyboard_with_high_confidence() {
        val c = DeviceClassifier.classify(null, emptyList(), emptyList(), 0x03C1)
        assertEquals("Keyboard", c.category); assertTrue(c.confidence >= 0.9)
        assertTrue(c.evidence.any { it.contains("appearance") })
    }

    @Test fun the_lovense_name_pattern_is_recognised_and_flagged() {
        val c = DeviceClassifier.classify("LVS-Lush", emptyList(), emptyList(), null)
        assertTrue(c.isLovense); assertEquals("Toy (Lovense)", c.category)
    }

    @Test fun sony_cameras_are_recognised_by_name_and_company() {
        val c = DeviceClassifier.classify("ILCE-6100", listOf(0x012D), emptyList(), null)
        assertEquals("Camera", c.category); assertEquals("Sony", c.vendor)
    }

    @Test fun a_company_id_alone_gives_a_vendor_but_not_a_category() {
        val c = DeviceClassifier.classify(null, listOf(0x004C), emptyList(), null)
        assertEquals(Classification.UNKNOWN, c.category); assertEquals("Apple", c.vendor)
        assertEquals(0.0, c.confidence, 0.0); assertFalse(c.evidence.isEmpty())
    }

    @Test fun nothing_known_is_unknown_with_zero_confidence() {
        val c = DeviceClassifier.classify(null, emptyList(), emptyList(), null)
        assertEquals(Classification.UNKNOWN, c.category); assertEquals(0.0, c.confidence, 0.0); assertNull(c.vendor)
    }

    @Test fun agreeing_clues_raise_confidence() {
        val one = DeviceClassifier.classify(null, emptyList(), listOf(0x1812), null)
        val two = DeviceClassifier.classify("Bluetooth keyboard", emptyList(), listOf(0x1812), null)
        assertTrue(two.confidence > one.confidence)
    }

    @Test fun the_most_confident_clue_wins_when_clues_disagree() {
        val c = DeviceClassifier.classify("mouse", emptyList(), emptyList(), 0x03C1)    // appearance says keyboard, name says mouse
        assertEquals("Keyboard", c.category)
    }
}

class DeviceLocatorTest {
    private val ref = -59.0
    private val n = 2.5

    /** Walks 10 m east then 6 m north in 1 m steps, hearing a device at (devX, devY) through a noisy path-loss channel. */
    private fun walk(devX: Double, devY: Double, noiseDb: Double, seed: Long): List<PathSample> {
        val rnd = Random(seed)
        val pts = (0..10).map { it.toDouble() to 0.0 } + (1..6).map { 10.0 to it.toDouble() }
        return pts.map { (x, y) ->
            val d = max(hypot(devX - x, devY - y), 0.5)
            PathSample(x, y, ref - 10 * n * log10(d) + rnd.nextGaussian() * noiseDb)
        }
    }

    private fun error(e: LocationEstimate, x: Double, y: Double) = hypot(e.x - x, e.y - y)

    @Test fun the_reported_error_radius_is_honest() {
        val results = (1L..60L).map { seed -> DeviceLocator.estimate(walk(8.0, 5.0, 3.0, seed), ref, n)!!.let { it to error(it, 8.0, 5.0) } }
        val errors = results.map { it.second }.sorted()
        val inside = results.count { it.second <= it.first.radiusMeters }
        val high = results.filter { it.first.confidence == LocateConfidence.High }
        val ambiguous = results.count { it.first.mirrorAmbiguous }
        val radii = results.map { it.first.radiusMeters }.sorted()
        println("locator, 3 dB noise, 60 walks: median error %.2f m, 90th pct %.2f m; median reported radius %.1f m; truth inside reported radius in %d of 60; ambiguous %d; High %d (worst High error %.2f m)"
            .format(errors[30], errors[54], radii[30], inside, ambiguous, high.size, high.maxOfOrNull { it.second } ?: 0.0))
        assertTrue("median error ${errors[30]}", errors[30] < 2.0)
        assertTrue("the true position should lie inside the reported radius most of the time (got $inside of 60)", inside >= 48)
        assertTrue("a High label must mean it, worst High error ${high.maxOfOrNull { it.second }}", high.all { it.second <= 3.5 })
    }
    @Test fun a_badly_wrong_reference_degrades_accuracy_but_the_error_radius_stays_honest() {
        // Reference level 6 dB off and no calibration: expect noticeably worse fixes on a short walk. What must hold is that
        // the app says so (a larger radius / lower confidence), not that it is secretly accurate.
        val results = (1L..40L).map { seed -> DeviceLocator.estimate(walk(8.0, 5.0, 3.0, seed), ref + 6, n)!!.let { it to error(it, 8.0, 5.0) } }
        val errors = results.map { it.second }.sorted()
        val inside = results.count { it.second <= it.first.radiusMeters }
        val high = results.filter { it.first.confidence == LocateConfidence.High }
        println("locator, 3 dB noise, reference 6 dB off, 40 walks: median error %.2f m, 90th pct %.2f m; truth inside reported radius in %d of 40; High %d (worst High error %.2f m)"
            .format(errors[20], errors[36], inside, high.size, high.maxOfOrNull { it.second } ?: 0.0))
        assertTrue("truth inside the reported radius in only $inside of 40", inside >= 28)
        assertTrue("a High label must not be wrong by more than 3.5 m", high.all { it.second <= 3.5 })
    }
    @Test fun heavy_noise_still_points_the_right_way_but_the_error_grows() {
        val errors = (1L..10L).map { seed -> error(DeviceLocator.estimate(walk(8.0, 5.0, 6.0, seed), ref, n)!!, 8.0, 5.0) }.sorted()
        println("locator error with 6 dB noise: median %.2f m, worst %.2f m".format(errors[errors.size / 2], errors.last()))
        assertTrue("median error ${errors[errors.size / 2]}", errors[errors.size / 2] < 6.0)
    }

    @Test fun a_straight_walk_is_flagged_as_left_right_ambiguous_and_low_confidence() {
        val straight = (0..12).map { PathSample(it.toDouble(), 0.0, ref - 10 * n * log10(max(hypot(8.0 - it, 5.0), 0.5))) }
        val e = DeviceLocator.estimate(straight, ref, n)!!
        assertTrue(e.mirrorAmbiguous); assertEquals(LocateConfidence.Low, e.confidence)
    }

    @Test fun too_little_data_or_movement_gives_no_estimate() {
        assertNull(DeviceLocator.estimate(List(3) { PathSample(it.toDouble(), 0.0, -60.0) }, ref, n))
        assertNull(DeviceLocator.estimate(List(10) { PathSample(0.1 * it, 0.0, -60.0) }, ref, n))     // barely moved
    }

    @Test fun walking_towards_the_device_makes_the_estimate_get_closer() {
        val all = walk(8.0, 5.0, 0.0, 1)
        val early = DeviceLocator.estimate(all.take(8), ref, n)!!
        val late = DeviceLocator.estimate(all, ref, n)!!
        val finalPos = all.last()
        assertTrue(hypot(late.x - finalPos.x, late.y - finalPos.y) < hypot(early.x - all[7].x, early.y - all[7].y) + 1.0)
    }
}

class MotionMathTest {
    @Test fun flat_phone_with_its_top_edge_north_heads_north() {
        val identity = floatArrayOf(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f)
        assertEquals(0.0, Heading.fromRotationMatrix(identity), 0.01)
    }

    @Test fun upright_phone_whose_back_faces_east_heads_east() {
        // device -> world: top edge points up, back of the phone points east
        val r = floatArrayOf(0f, 0f, -1f,  -1f, 0f, 0f,  0f, 1f, 0f)
        assertEquals(90.0, Heading.fromRotationMatrix(r), 0.01)
    }

    @Test fun step_detector_counts_a_two_hertz_walk() {
        val det = StepDetector()
        var steps = 0
        for (i in 0 until 500) {                               // 10 s at 50 Hz, 2 steps per second
            val t = i * 20L
            val az = 9.81 + 3.0 * sin(2 * Math.PI * 2.0 * t / 1000.0)
            if (det.feed(0.0, 0.0, az, t)) steps++
        }
        assertTrue("counted $steps", steps in 18..22)
    }

    @Test fun standing_still_counts_no_steps() {
        val det = StepDetector()
        var steps = 0
        for (i in 0 until 500) if (det.feed(0.0, 0.0, 9.81 + 0.05 * sin(i / 3.0), i * 20L)) steps++
        assertEquals(0, steps)
    }

    @Test fun smoothing_takes_the_short_way_round_north() {
        assertEquals(355.0, Compass.smooth(350.0, 10.0, 0.25), 0.01)       // not 340: it crosses north, not south
        assertEquals(5.0, Compass.smooth(10.0, 350.0, 0.25), 0.01)
        assertEquals(90.0, Compass.smooth(90.0, 90.0, 0.5), 0.01)
    }

    @Test fun smoothing_converges_on_the_new_heading() {
        var h = 0.0
        repeat(60) { h = Compass.smooth(h, 270.0, 0.15) }
        assertEquals(270.0, h, 1.0)
    }

    @Test fun compass_labels_and_turn_direction() {
        assertEquals("N", Compass.label(0.0)); assertEquals("N", Compass.label(359.0)); assertEquals("E", Compass.label(90.0))
        assertEquals("SW", Compass.label(225.0)); assertEquals("W", Compass.label(271.0))
        assertEquals(90.0, Compass.turnTo(90.0, 0.0), 0.01)            // target to the east, facing north: turn right
        assertEquals(-90.0, Compass.turnTo(270.0, 0.0), 0.01)          // target to the west: turn left
        assertEquals(-20.0, Compass.turnTo(350.0, 10.0), 0.01)         // across the 0/360 seam
    }

    @Test fun dead_reckoning_moves_along_the_heading() {
        val dr = DeadReckoner(0.7)
        repeat(10) { dr.step(90.0) }
        assertEquals(7.0, dr.x, 1e-9); assertEquals(0.0, dr.y, 1e-9)
        repeat(10) { dr.step(0.0) }
        assertEquals(7.0, dr.y, 1e-9); assertEquals(20, dr.steps)
    }
}
