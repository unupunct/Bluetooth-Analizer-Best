package com.unupunct.fleetmonitor.domain

import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/** Pure maths behind the walking tracker, so it can be unit tested without a phone. */
object Heading {
    /**
     * Compass heading in degrees clockwise from north (0..360) of the direction the user is facing, from the 3x3 rotation matrix
     * Android's rotation-vector sensor gives (device -> world, X east, Y north, Z up, row-major).
     * Held upright (the usual way to walk and look at the screen) the user faces where the back of the phone points; held flat
     * the back points at the floor, so the top edge of the phone is used instead.
     */
    fun fromRotationMatrix(r: FloatArray): Double {
        val backZ = -r[8].toDouble()                       // vertical part of the back-of-phone direction (-Z in device coordinates)
        val east: Double; val north: Double
        if (kotlin.math.abs(backZ) > 0.8) { east = r[1].toDouble(); north = r[4].toDouble() }   // phone flat: use the top edge (+Y)
        else { east = -r[2].toDouble(); north = -r[5].toDouble() }                                 // upright: use the back (-Z)
        val deg = Math.toDegrees(atan2(east, north))
        return (deg + 360.0) % 360.0
    }
}

/** Compass helpers. */
object Compass {
    /** Moves [prev] a fraction [alpha] of the way to [next] along the SHORTEST arc, so 350 to 10 goes through north, not through south. */
    fun smooth(prev: Double, next: Double, alpha: Double): Double {
        var diff = ((next - prev + 540.0) % 360.0) - 180.0
        return ((prev + alpha * diff) % 360.0 + 360.0) % 360.0
    }

    fun label(deg: Double): String = listOf("N", "NE", "E", "SE", "S", "SW", "W", "NW")[(((deg % 360.0) + 360.0 + 22.5) / 45.0).toInt() % 8]

    /** Signed turn needed to face [bearing] when facing [heading]: negative = turn left, positive = turn right, in -180..180. */
    fun turnTo(bearing: Double, heading: Double): Double = ((bearing - heading + 540.0) % 360.0) - 180.0
}

/** Counts steps from accelerometer samples (no permission needed, unlike the platform step sensors). */
class StepDetector(private val thresholdMs2: Double = 1.1, private val minIntervalMs: Long = 280) {
    private var lowPass = 0.0
    private var armed = true
    private var lastStepMs = -1_000_000L
    private var gravity = 9.81

    /** Returns true when this sample completes a step. */
    fun feed(ax: Double, ay: Double, az: Double, tMs: Long): Boolean {
        val mag = sqrt(ax * ax + ay * ay + az * az)
        gravity += 0.02 * (mag - gravity)                  // slowly track gravity so the phone can be held at any angle
        lowPass += 0.35 * ((mag - gravity) - lowPass)
        if (armed && lowPass > thresholdMs2 && tMs - lastStepMs >= minIntervalMs) {
            armed = false; lastStepMs = tMs
            return true
        }
        if (!armed && lowPass < thresholdMs2 * 0.3) armed = true
        return false
    }
}

/** Dead reckoning: each step moves the position one stride in the heading direction. Drift grows with distance walked. */
class DeadReckoner(var strideMeters: Double = 0.7) {
    var x = 0.0; private set
    var y = 0.0; private set
    var steps = 0; private set

    fun step(headingDeg: Double) {
        val h = Math.toRadians(headingDeg)
        x += strideMeters * sin(h)
        y += strideMeters * cos(h)
        steps++
    }

    fun reset() { x = 0.0; y = 0.0; steps = 0 }
}
