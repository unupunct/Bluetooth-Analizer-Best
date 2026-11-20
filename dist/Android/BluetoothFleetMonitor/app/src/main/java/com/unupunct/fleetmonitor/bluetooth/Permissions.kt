package com.unupunct.fleetmonitor.bluetooth

import android.Manifest
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import androidx.core.content.ContextCompat

object Permissions {
    /** Android 12+: Nearby devices (BLUETOOTH_SCAN, declared neverForLocation). Android 10-11: fine location is mandatory for BLE scans. */
    fun scanPermissions(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) arrayOf(Manifest.permission.BLUETOOTH_SCAN)
        else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)

    fun requestable(): Array<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) scanPermissions() + Manifest.permission.POST_NOTIFICATIONS
        else scanPermissions()

    fun hasScanPermission(c: Context) = scanPermissions().all { ContextCompat.checkSelfPermission(c, it) == PackageManager.PERMISSION_GRANTED }

    fun hasNotificationPermission(c: Context) =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(c, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    fun bluetoothEnabled(c: Context): Boolean =
        (c.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter?.isEnabled == true

    fun hasBluetoothLe(c: Context) = c.packageManager.hasSystemFeature(PackageManager.FEATURE_BLUETOOTH_LE)

    /** Android 10 and 11 refuse to deliver BLE scan results while the Location setting is off. */
    fun locationServicesOn(c: Context): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) return true
        val lm = c.getSystemService(Context.LOCATION_SERVICE) as android.location.LocationManager
        return androidx.core.location.LocationManagerCompat.isLocationEnabled(lm)
    }

    fun ignoringBatteryOptimisations(c: Context): Boolean =
        (c.getSystemService(Context.POWER_SERVICE) as PowerManager).isIgnoringBatteryOptimizations(c.packageName)
}
