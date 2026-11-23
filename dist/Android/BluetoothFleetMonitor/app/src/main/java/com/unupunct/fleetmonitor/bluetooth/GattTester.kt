package com.unupunct.fleetmonitor.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.Context
import com.unupunct.fleetmonitor.domain.AdParser
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

class GattResult(
    val outcome: String,
    val gattStatus: Int?,
    val detail: String,
    val durationMs: Long,
    val services: List<String>,
    val name: String?,
    val manufacturer: String?,
    val model: String?,
    val firmware: String?,
    val appearance: Int?,
    val rssiReads: List<Int>,
) {
    val success: Boolean get() = outcome == "Success"
}

object GattStatus {
    fun describe(code: Int?): String = when (code) {
        null -> "no status"
        0 -> "success"
        8 -> "connection timed out (the link was lost or the device went quiet)"
        19 -> "the device ended the connection"
        22 -> "this phone ended the connection"
        34 -> "the device stopped answering during link setup"
        62 -> "the connection could not be established"
        133 -> "generic GATT error: the device is out of range, busy with another app, or refused the connection"
        else -> "GATT status $code"
    }
}

/**
 * Connects to ONE device the user chose, reads only the standard, read-only identification characteristics (Generic Access
 * name and appearance, Device Information maker/model/firmware), samples the link RSSI for a few seconds, and disconnects.
 * It never writes, never pairs, never subscribes and never sends any command. Tests run one at a time with a cooldown.
 */
@Singleton
class GattTester @Inject constructor(@ApplicationContext private val context: Context) {
    private val lock = Mutex()
    private val lastEnd = HashMap<String, Long>()

    private val GAP = uuid16(0x1800); private val DEVICE_NAME = uuid16(0x2A00); private val APPEARANCE = uuid16(0x2A01)
    private val DIS = uuid16(0x180A); private val MANUFACTURER = uuid16(0x2A29); private val MODEL = uuid16(0x2A24); private val FIRMWARE = uuid16(0x2A26)

    private fun uuid16(v: Int) = UUID.fromString("0000%04x-0000-1000-8000-00805f9b34fb".format(v))

    fun cooldownRemainingMs(address: String): Long = ((lastEnd[address] ?: 0L) + COOLDOWN_MS - System.currentTimeMillis()).coerceAtLeast(0)

    @SuppressLint("MissingPermission")
    suspend fun test(address: String, rssiSeconds: Int = 5, connectTimeoutMs: Long = 15_000): GattResult = lock.withLock {
        val started = System.currentTimeMillis()
        fun fail(outcome: String, detail: String, status: Int? = null) =
            GattResult(outcome, status, detail, System.currentTimeMillis() - started, emptyList(), null, null, null, null, null, emptyList())

        if (!Permissions.hasConnectPermission(context)) return fail("PermissionDenied", "Bluetooth connect permission has not been granted.")
        if (!Permissions.bluetoothEnabled(context)) return fail("Error", "Bluetooth is turned off.")
        val wait = cooldownRemainingMs(address)
        if (wait > 0) return fail("Cooldown", "Wait ${wait / 1000 + 1} s before testing this device again.")

        val adapter: BluetoothAdapter = (context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter
        val colon = address.chunked(2).joinToString(":")
        val device: BluetoothDevice = try { adapter.getRemoteDevice(colon) } catch (e: IllegalArgumentException) { return fail("Error", "Not a valid Bluetooth address.") }

        val connected = CompletableDeferred<Int>()
        val discovered = CompletableDeferred<Int>()
        var pendingRead = CompletableDeferred<Pair<Int, ByteArray?>>()
        var pendingRssi = CompletableDeferred<Pair<Int, Int>>()
        val disconnected = CompletableDeferred<Unit>()

        val callback = object : BluetoothGattCallback() {
            override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
                if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) connected.complete(status)
                else if (newState == BluetoothProfile.STATE_DISCONNECTED) { connected.complete(if (status == 0) -1 else status); disconnected.complete(Unit) }
            }
            override fun onServicesDiscovered(g: BluetoothGatt, status: Int) { discovered.complete(status) }
            // API 33+ delivers the value directly; older versions put it on the characteristic. Both are handled.
            override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray, status: Int) { pendingRead.complete(status to value) }
            @Deprecated("API < 33") override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) {
                @Suppress("DEPRECATION") pendingRead.complete(status to c.value)
            }
            override fun onReadRemoteRssi(g: BluetoothGatt, rssi: Int, status: Int) { pendingRssi.complete(status to rssi) }
        }

        val gatt = try { device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE) }
                   catch (e: SecurityException) { return fail("PermissionDenied", "Permission was revoked.") }
        try {
            val status = withTimeoutOrNull(connectTimeoutMs) { connected.await() }
                ?: return fail("Timeout", "No connection within ${connectTimeoutMs / 1000} s. The device may be out of range, asleep, or busy with another app.", 8)
            if (status != BluetoothGatt.GATT_SUCCESS) return fail("ConnectFailed", GattStatus.describe(if (status == -1) null else status), status)

            gatt.discoverServices()
            val dStatus = withTimeoutOrNull(10_000) { discovered.await() } ?: return fail("DiscoveryFailed", "The device did not list its services in time.")
            if (dStatus != BluetoothGatt.GATT_SUCCESS) return fail("DiscoveryFailed", GattStatus.describe(dStatus), dStatus)
            val serviceList = gatt.services.map { it.uuid.toString() }

            suspend fun read(service: UUID, characteristic: UUID): ByteArray? {
                val ch = gatt.getService(service)?.getCharacteristic(characteristic) ?: return null
                if (ch.properties and BluetoothGattCharacteristic.PROPERTY_READ == 0) return null
                pendingRead = CompletableDeferred()
                if (!gatt.readCharacteristic(ch)) return null
                val (st, v) = withTimeoutOrNull(4_000) { pendingRead.await() } ?: return null
                return if (st == BluetoothGatt.GATT_SUCCESS) v else null     // protected or unreadable: skip, not an error
            }
            fun text(b: ByteArray?) = b?.toString(Charsets.UTF_8)?.trim { it <= ' ' }?.takeIf { it.isNotEmpty() }
            val name = text(read(GAP, DEVICE_NAME))
            val appearance = read(GAP, APPEARANCE)?.takeIf { it.size >= 2 }?.let { (it[0].toInt() and 0xFF) or ((it[1].toInt() and 0xFF) shl 8) }
            val maker = text(read(DIS, MANUFACTURER)); val model = text(read(DIS, MODEL)); val fw = text(read(DIS, FIRMWARE))

            val rssi = ArrayList<Int>()
            repeat(rssiSeconds) {
                pendingRssi = CompletableDeferred()
                if (gatt.readRemoteRssi()) withTimeoutOrNull(2_000) { pendingRssi.await() }?.let { (st, r) -> if (st == BluetoothGatt.GATT_SUCCESS) rssi.add(r) }
                delay(1000)
            }
            GattResult("Success", 0, "Connected and read the standard identification data.", System.currentTimeMillis() - started, serviceList, name, maker, model, fw, appearance, rssi)
        } finally {
            runCatching { gatt.disconnect() }
            withTimeoutOrNull(2_000) { disconnected.await() }
            runCatching { gatt.close() }
            lastEnd[address] = System.currentTimeMillis()
        }
    }

    companion object { const val COOLDOWN_MS = 10_000L }
}
