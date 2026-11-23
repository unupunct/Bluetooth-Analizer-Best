package com.unupunct.fleetmonitor.ui

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

/** Short vibration pulses for "follow the signal". */
object Haptics {
    private fun vibrator(ctx: Context): Vibrator? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) ctx.getSystemService(VibratorManager::class.java)?.defaultVibrator
        else @Suppress("DEPRECATION") ctx.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator

    fun pulse(ctx: Context, ms: Long = 45) {
        val v = vibrator(ctx) ?: return
        if (!v.hasVibrator()) return
        v.vibrate(VibrationEffect.createOneShot(ms, VibrationEffect.DEFAULT_AMPLITUDE))
    }

    /** Closer means faster: about 0.3 s between pulses at 0.5 m, 2.5 s at 10 m or more. */
    fun intervalForDistance(meters: Double): Long = (300 + meters * 230).toLong().coerceIn(300, 2500)
}
