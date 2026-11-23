package com.unupunct.fleetmonitor.bluetooth

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import com.unupunct.fleetmonitor.domain.DeadReckoner
import com.unupunct.fleetmonitor.domain.Heading
import com.unupunct.fleetmonitor.domain.StepDetector
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject
import javax.inject.Singleton

/** Where the phone has walked since the path was last reset (metres east/north of the start) and which way it faces. */
class MotionState(val x: Double = 0.0, val y: Double = 0.0, val headingDeg: Double = 0.0, val steps: Int = 0, val sensorsOn: Boolean = false, val hasHeading: Boolean = false)

/**
 * Dead-reckoning from the phone's own sensors: accelerometer step detection plus compass heading from the rotation-vector sensor.
 * Neither needs a permission. Position drifts with distance walked (a few percent of the path plus heading error), so the path
 * is only trusted for the short walks used to find a device, and the radar says so.
 */
@Singleton
class MotionTracker @Inject constructor(@ApplicationContext context: Context) : SensorEventListener {
    private val sm = context.getSystemService(Context.SENSOR_SERVICE) as SensorManager
    private val accel: Sensor? = sm.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
    private val rotation: Sensor? = sm.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)

    private val steps = StepDetector()
    private val walk = DeadReckoner()
    private val matrix = FloatArray(9)
    private var heading = 0.0
    private var haveHeading = false
    private var lastPublish = 0L
    private val _state = MutableStateFlow(MotionState())
    val state: StateFlow<MotionState> = _state.asStateFlow()

    val available: Boolean get() = accel != null && rotation != null

    fun start(strideMeters: Double) {
        walk.strideMeters = strideMeters
        if (_state.value.sensorsOn) return
        accel?.let { sm.registerListener(this, it, SensorManager.SENSOR_DELAY_GAME) }
        rotation?.let { sm.registerListener(this, it, SensorManager.SENSOR_DELAY_GAME) }
        publish(true)
    }

    fun stop() {
        sm.unregisterListener(this)
        _state.value = MotionState(walk.x, walk.y, heading, walk.steps, sensorsOn = false, hasHeading = haveHeading)
    }

    /** Start a new walk from here. */
    fun resetPath() { walk.reset(); publish(true) }

    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_ROTATION_VECTOR -> {
                SensorManager.getRotationMatrixFromVector(matrix, e.values)
                val raw = Heading.fromRotationMatrix(matrix)
                heading = if (haveHeading) com.unupunct.fleetmonitor.domain.Compass.smooth(heading, raw, 0.15) else raw
                haveHeading = true
            }
            Sensor.TYPE_ACCELEROMETER ->
                if (haveHeading && steps.feed(e.values[0].toDouble(), e.values[1].toDouble(), e.values[2].toDouble(), e.timestamp / 1_000_000)) walk.step(heading)
        }
        val now = System.currentTimeMillis()
        if (now - lastPublish >= 100) { lastPublish = now; publish(true) }
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}

    private fun publish(on: Boolean) { _state.value = MotionState(walk.x, walk.y, heading, walk.steps, on, haveHeading) }
}
