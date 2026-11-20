package com.unupunct.fleetmonitor.domain

enum class ScannerState { Stopped, Starting, Running, RunningNoAdvertisements, Interrupted, Error }

class ScanStatus(val state: ScannerState, val message: String, val lastAdvertisementMs: Long?, val changedMs: Long)

/** A period in which the phone could not hear anything. Devices are never blamed for silence inside one. */
data class CoverageGap(val startMs: Long, val endMs: Long?, val reason: String)

enum class DevicePresence { Detected, Missing, Uncertain }

object PresenceStatus {
    /** "Missing" only means "not heard recently while the scanner demonstrably could hear". Never "disconnected". */
    fun evaluate(lastSeenMs: Long?, nowMs: Long, windowMs: Long, scannerRunning: Boolean, gaps: List<CoverageGap>): DevicePresence {
        if (lastSeenMs == null) return DevicePresence.Uncertain
        if (nowMs - lastSeenMs <= windowMs) return DevicePresence.Detected
        if (!scannerRunning) return DevicePresence.Uncertain
        for (g in gaps) {
            val end = g.endMs ?: Long.MAX_VALUE
            if (g.startMs <= nowMs && end >= lastSeenMs) return DevicePresence.Uncertain
        }
        return DevicePresence.Missing
    }
}

/** Pure state machine, mirrors the Windows ScannerHealth: honest status plus coverage gaps. */
class ScanHealth(private val noAdvertisementMs: Long, private val clock: () -> Long = System::currentTimeMillis) {
    private val gapList = ArrayList<CoverageGap>()
    private var running = false
    private var stoppedByUser = true
    private var startedMs = 0L
    private var lastAdMs: Long? = null
    private var problem: String? = null
    private var changedMs = clock()

    val gaps: List<CoverageGap> @Synchronized get() = gapList.toList()

    @Synchronized fun started() {
        val now = clock()
        closeOpenGap(now)
        running = true; stoppedByUser = false; problem = null; startedMs = now; changedMs = now
    }

    @Synchronized fun advertisementReceived() { lastAdMs = clock() }

    @Synchronized fun stopped(requestedByUser: Boolean, reason: String?) {
        val now = clock()
        running = false; stoppedByUser = requestedByUser; problem = reason; changedMs = now
        if (!requestedByUser) openGap(now, reason ?: "Scanner stopped unexpectedly")
    }

    /** Records a gap that already ended, e.g. "the app was not running between two heartbeats". */
    @Synchronized fun recordPastGap(startMs: Long, endMs: Long, reason: String) { gapList.add(CoverageGap(startMs, endMs, reason)) }

    @Synchronized fun current(): ScanStatus {
        val now = clock()
        if (!running) {
            return if (stoppedByUser) ScanStatus(ScannerState.Stopped, "Scanner stopped", lastAdMs, changedMs)
            else ScanStatus(ScannerState.Interrupted, "Scanner was interrupted: ${problem ?: "unknown reason"}", lastAdMs, changedMs)
        }
        val reference = lastAdMs ?: startedMs
        if (now - reference > noAdvertisementMs)
            return ScanStatus(ScannerState.RunningNoAdvertisements, DRIVER_HINT, lastAdMs, changedMs)
        return ScanStatus(ScannerState.Running, if (lastAdMs == null) "Listening, waiting for the first advertisement" else "Listening", lastAdMs, changedMs)
    }

    private fun openGap(now: Long, reason: String) {
        if (gapList.isNotEmpty() && gapList.last().endMs == null) return
        gapList.add(CoverageGap(now, null, reason))
    }

    private fun closeOpenGap(now: Long) {
        if (gapList.isNotEmpty() && gapList.last().endMs == null) gapList[gapList.size - 1] = gapList.last().copy(endMs = now)
    }

    companion object {
        const val DRIVER_HINT = "The scanner is running but has heard no advertisements. Check that Bluetooth is on, that location " +
            "services are on (Android 10 and 11 need them for scanning), that the screen has not paused a filtered scan, and that " +
            "devices are nearby. This is not proof that the devices are off."
    }
}
