package com.unupunct.fleetmonitor.data

import android.content.Context
import android.content.SharedPreferences
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject
import javax.inject.Singleton

enum class ScanMode(val label: String) {
    Continuous("Continuous"),
    Background("Background"),
    Inspection("Inspection"),
}

data class AppSettings(
    val sampleContinuousSec: Int = 10,
    val sampleInspectionSec: Int = 2,
    val sampleBackgroundSec: Int = 30,
    val presenceWindowSec: Int = 120,
    /** Comma-separated EXACT device names for Background mode (Android pauses unfiltered scans when the screen is off). */
    val backgroundDeviceNames: String = "",
    val lastLocation: String = "Office",
)

/** Plain SharedPreferences behind a StateFlow: tiny, synchronous, and survives process death. */
@Singleton
class SettingsStore @Inject constructor(@ApplicationContext context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("fleet_settings", Context.MODE_PRIVATE)
    private val _settings = MutableStateFlow(read())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()

    private fun read() = AppSettings(
        sampleContinuousSec = prefs.getInt("sampleContinuousSec", 10),
        sampleInspectionSec = prefs.getInt("sampleInspectionSec", 2),
        sampleBackgroundSec = prefs.getInt("sampleBackgroundSec", 30),
        presenceWindowSec = prefs.getInt("presenceWindowSec", 120),
        backgroundDeviceNames = prefs.getString("backgroundDeviceNames", "") ?: "",
        lastLocation = prefs.getString("lastLocation", "Office") ?: "Office",
    )

    fun update(block: (AppSettings) -> AppSettings) {
        val s = block(_settings.value)
        prefs.edit()
            .putInt("sampleContinuousSec", s.sampleContinuousSec.coerceIn(0, 600))
            .putInt("sampleInspectionSec", s.sampleInspectionSec.coerceIn(0, 600))
            .putInt("sampleBackgroundSec", s.sampleBackgroundSec.coerceIn(0, 3600))
            .putInt("presenceWindowSec", s.presenceWindowSec.coerceIn(10, 86400))
            .putString("backgroundDeviceNames", s.backgroundDeviceNames)
            .putString("lastLocation", s.lastLocation)
            .apply()
        _settings.value = read()
    }

    // ---- scanning intent, kept so a killed process can tell it was interrupted ----
    var wantScanning: Boolean
        get() = prefs.getBoolean("wantScanning", false)
        set(v) { prefs.edit().putBoolean("wantScanning", v).apply() }

    var scanMode: ScanMode
        get() = runCatching { ScanMode.valueOf(prefs.getString("scanMode", ScanMode.Continuous.name)!!) }.getOrDefault(ScanMode.Continuous)
        set(v) { prefs.edit().putString("scanMode", v.name).apply() }

    var heartbeatMs: Long
        get() = prefs.getLong("heartbeatMs", 0L)
        set(v) { prefs.edit().putLong("heartbeatMs", v).apply() }

    fun sampleIntervalMs(mode: ScanMode): Long = when (mode) {
        ScanMode.Continuous -> _settings.value.sampleContinuousSec
        ScanMode.Inspection -> _settings.value.sampleInspectionSec
        ScanMode.Background -> _settings.value.sampleBackgroundSec
    } * 1000L
}
