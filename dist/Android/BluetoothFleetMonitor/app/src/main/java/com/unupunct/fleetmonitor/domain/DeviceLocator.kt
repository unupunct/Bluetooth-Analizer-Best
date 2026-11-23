package com.unupunct.fleetmonitor.domain

import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.hypot
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.math.sqrt

/** One point along the walked path: where the phone was (metres east/north of the start) and the smoothed RSSI heard there. */
class PathSample(val x: Double, val y: Double, val rssi: Double)

enum class LocateConfidence(val label: String) { Low("low"), Medium("medium"), High("high") }

class LocationEstimate(
    /** Best-fit position, metres east/north of where the walk started. */
    val x: Double,
    val y: Double,
    /** RSSI at 1 m that best explains the walk (only meaningfully different from the starting guess when the walk is long enough). */
    val rssiAt1m: Double,
    val residualDb: Double,
    val pathExtentMeters: Double,
    /**
     * Radius of the region of positions the data cannot rule out (about 2 sigma). The device is probably within this
     * distance of ([x], [y]). It shrinks as the walk gets longer and turns.
     */
    val radiusMeters: Double,
    /** True when the data fit two or more separate regions (typically a mirror image across a straight-ish path). */
    val mirrorAmbiguous: Boolean,
    /** Centre of the other region, when [mirrorAmbiguous]. Walking in a different direction resolves it. */
    val alternativeX: Double? = null,
    val alternativeY: Double? = null,
    val confidence: LocateConfidence,
)

/**
 * Finds where a device is from a walk. A phone's Bluetooth radio has no direction sensor, so direction comes entirely from
 * how the signal changed as the phone moved. The search looks for positions (and a reference signal level) whose predicted
 * RSSI along the path matches what was heard, and reports not just the best one but the whole region the data cannot rule
 * out, so the answer carries an honest error radius.
 */
object DeviceLocator {
    private const val MIN_SAMPLES = 6
    private const val MIN_EXTENT_M = 2.0
    private const val REF_TOLERANCE_DB = 12.0
    private const val GRID_STEP = 2.0
    private const val GRID_RADIUS = 60.0
    /** Positions within this many noise-variances of the best fit are "not ruled out" (about a 95% region for two position parameters, with margin). */
    private const val REGION_K = 8.0
    private const val MIN_NOISE_DB = 2.0

    fun estimate(samples: List<PathSample>, referenceRssi1m: Double, n: Double): LocationEstimate? {
        if (samples.size < MIN_SAMPLES) return null
        val extent = extent(samples)
        if (extent < MIN_EXTENT_M) return null

        val cx = samples.map { it.x }.average(); val cy = samples.map { it.y }.average()
        val steps = (GRID_RADIUS / GRID_STEP).roundToInt()
        val grid = HashMap<Long, Candidate>()
        fun key(ix: Int, iy: Int) = (ix.toLong() shl 32) xor (iy.toLong() and 0xffffffffL)
        for (ix in -steps..steps) for (iy in -steps..steps)
            grid[key(ix, iy)] = evaluate(samples, referenceRssi1m, n, cx + ix * GRID_STEP, cy + iy * GRID_STEP)

        // Refine the few best grid points to a precise best fit.
        val seeds = ArrayList<Candidate>()
        for (c in grid.values.sortedBy { it.cost }) { if (seeds.none { hypot(it.x - c.x, it.y - c.y) < 8.0 }) seeds.add(c); if (seeds.size == 5) break }
        val best = seeds.map { s ->
            var b = search(samples, referenceRssi1m, n, s, s.x, s.y, 3.0, 0.5)
            b = search(samples, referenceRssi1m, n, b, b.x, b.y, 0.6, 0.1)
            b
        }.minByOrNull { it.cost } ?: return null

        val rms = sqrt(best.cost / samples.size)
        val noiseVar = max(best.cost / samples.size, MIN_NOISE_DB * MIN_NOISE_DB)
        val limit = best.cost + REGION_K * noiseVar

        // Region the data cannot rule out, split into connected clusters of grid cells.
        val inRegion = HashMap<Long, Pair<Int, Int>>()
        for (ix in -steps..steps) for (iy in -steps..steps) if (grid.getValue(key(ix, iy)).cost <= limit) inRegion[key(ix, iy)] = ix to iy
        val clusterOf = HashMap<Long, Int>()
        val clusters = ArrayList<List<Long>>()
        for (k0 in inRegion.keys) {
            if (clusterOf.containsKey(k0)) continue
            val id = clusters.size; val members = ArrayList<Long>(); val queue = ArrayDeque<Long>()
            queue.add(k0); clusterOf[k0] = id
            while (queue.isNotEmpty()) {
                val k = queue.removeFirst(); members.add(k)
                val (ix, iy) = inRegion.getValue(k)
                for (dx in -1..1) for (dy in -1..1) {
                    val nk = key(ix + dx, iy + dy)
                    if (inRegion.containsKey(nk) && !clusterOf.containsKey(nk)) { clusterOf[nk] = id; queue.add(nk) }
                }
            }
            clusters.add(members)
        }
        fun pos(k: Long): Pair<Double, Double> { val (ix, iy) = inRegion.getValue(k); return (cx + ix * GRID_STEP) to (cy + iy * GRID_STEP) }
        val bestIx = ((best.x - cx) / GRID_STEP).roundToInt().coerceIn(-steps, steps)
        val bestIy = ((best.y - cy) / GRID_STEP).roundToInt().coerceIn(-steps, steps)
        val homeId = clusterOf[key(bestIx, bestIy)]
            ?: clusters.indices.minByOrNull { id -> clusters[id].minOf { k -> val p = pos(k); hypot(p.first - best.x, p.second - best.y) } }
        val home = if (homeId != null) clusters[homeId] else emptyList()
        // Radius of the home region measured from the best fit, plus half a grid cell because the cells are coarse.
        val radius = (home.maxOfOrNull { k -> val p = pos(k); hypot(p.first - best.x, p.second - best.y) } ?: 0.0) + GRID_STEP / 2

        var altX: Double? = null; var altY: Double? = null
        var altCost = Double.MAX_VALUE
        for ((id, members) in clusters.withIndex()) {
            if (id == homeId) continue
            val bestCell = members.minByOrNull { grid.getValue(it).cost } ?: continue
            val p = pos(bestCell)
            if (hypot(p.first - best.x, p.second - best.y) >= 4.0 + radius && grid.getValue(bestCell).cost < altCost) { altCost = grid.getValue(bestCell).cost; altX = p.first; altY = p.second }
        }
        val ambiguous = altX != null || minorExtent(samples) < MIN_EXTENT_M

        val confidence = when {
            ambiguous || radius > 5.0 || extent < 4.0 -> LocateConfidence.Low
            radius > 2.5 || extent < 8.0 || rms > 5.0 -> LocateConfidence.Medium
            else -> LocateConfidence.High
        }
        return LocationEstimate(best.x, best.y, best.ref, rms, extent, radius, ambiguous, altX, altY, confidence)
    }

    private class Candidate(val x: Double, val y: Double, val ref: Double, val cost: Double)

    private fun search(s: List<PathSample>, ref0: Double, n: Double, start: Candidate, ox: Double, oy: Double, radius: Double, step: Double): Candidate {
        var best = start
        var x = ox - radius
        while (x <= ox + radius + 1e-9) {
            var y = oy - radius
            while (y <= oy + radius + 1e-9) {
                val c = evaluate(s, ref0, n, x, y)
                if (c.cost < best.cost) best = c
                y += step
            }
            x += step
        }
        return best
    }

    /** For a candidate position the best reference level has a closed form (mean of rssi + 10 n log d); it is kept near the starting guess. */
    private fun evaluate(s: List<PathSample>, ref0: Double, n: Double, px: Double, py: Double): Candidate {
        val t = DoubleArray(s.size)
        for (i in s.indices) {
            val d = max(hypot(px - s[i].x, py - s[i].y), 0.5)
            t[i] = s[i].rssi + 10.0 * n * log10(d)
        }
        val mean = t.average()
        val ref = mean.coerceIn(ref0 - REF_TOLERANCE_DB, ref0 + REF_TOLERANCE_DB)
        var cost = 0.0
        for (v in t) { val e = v - ref; cost += e * e }
        cost += 0.1 * (ref - ref0) * (ref - ref0)    // very gentle pull towards the starting reference
        return Candidate(px, py, ref, cost)
    }

    /** Largest distance between any two path points. */
    fun extent(s: List<PathSample>): Double {
        var m = 0.0
        for (i in s.indices) for (j in i + 1 until s.size) m = max(m, hypot(s[i].x - s[j].x, s[i].y - s[j].y))
        return m
    }

    /** Spread of the path across its own main direction: near zero for a straight walk. */
    fun minorExtent(s: List<PathSample>): Double {
        val mx = s.map { it.x }.average(); val my = s.map { it.y }.average()
        var sxx = 0.0; var syy = 0.0; var sxy = 0.0
        for (p in s) { val dx = p.x - mx; val dy = p.y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy }
        val theta = 0.5 * atan2(2 * sxy, sxx - syy)
        val ux = kotlin.math.cos(theta); val uy = kotlin.math.sin(theta)
        var lo = Double.MAX_VALUE; var hi = -Double.MAX_VALUE
        for (p in s) { val v = -(p.x - mx) * uy + (p.y - my) * ux; lo = minOf(lo, v); hi = maxOf(hi, v) }
        return abs(hi - lo)
    }
}
