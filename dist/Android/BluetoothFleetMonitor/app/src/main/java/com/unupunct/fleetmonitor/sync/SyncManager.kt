package com.unupunct.fleetmonitor.sync

import android.content.Context
import android.os.Build
import com.unupunct.fleetmonitor.data.ConnectionTestEntity
import com.unupunct.fleetmonitor.data.FleetRepository
import com.unupunct.fleetmonitor.data.ObservationEntity
import com.unupunct.fleetmonitor.data.SessionEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

data class SyncState(
    val paired: Boolean = false,
    val serverName: String = "",
    val host: String = "",
    val port: Int = 0,
    val fingerprint: String = "",
    val autoSync: Boolean = true,
    val syncing: Boolean = false,
    val lastSyncMs: Long? = null,
    val lastMessage: String = "",
    val lastError: String? = null,
    val clockSkewSeconds: Double? = null,
)

/**
 * Pairing and uploading to the Windows app. Observations are only marked as uploaded after the PC confirms it stored them (HTTP 200),
 * so a failed or interrupted upload loses nothing and is simply retried. Data on the phone is never deleted by syncing.
 */
@Singleton
class SyncManager @Inject constructor(@ApplicationContext private val context: Context, private val repo: FleetRepository) {
    private val prefs = context.getSharedPreferences("sync", Context.MODE_PRIVATE)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutex = Mutex()
    private var started = false

    private val _state = MutableStateFlow(load())
    val state: StateFlow<SyncState> = _state.asStateFlow()

    /** A pairing link that arrived (QR scan, pasted, or opened from another app) and waits for the user to confirm. */
    private val _pending = MutableStateFlow<PairingLink?>(null)
    val pendingPair: StateFlow<PairingLink?> = _pending.asStateFlow()
    private val _pairMessage = MutableStateFlow<String?>(null)
    val pairMessage: StateFlow<String?> = _pairMessage.asStateFlow()

    private fun load() = SyncState(
        paired = prefs.getString("token", null) != null,
        serverName = prefs.getString("serverName", "") ?: "", host = prefs.getString("host", "") ?: "", port = prefs.getInt("port", 0),
        fingerprint = prefs.getString("fp", "") ?: "", autoSync = prefs.getBoolean("auto", true),
        lastSyncMs = prefs.getLong("lastSync", 0).takeIf { it > 0 }, lastMessage = prefs.getString("lastMessage", "") ?: "",
    )

    private fun phoneId(): String = prefs.getString("phoneId", null) ?: UUID.randomUUID().toString().also { prefs.edit().putString("phoneId", it).apply() }
    private fun phoneName(): String = listOf(Build.MANUFACTURER, Build.MODEL).filter { !it.isNullOrBlank() }.joinToString(" ").take(60).ifBlank { "Android phone" }

    /** Starts the background loop (idempotent). Runs for as long as the app process lives, which includes a running scan service. */
    fun start() {
        if (started) return
        started = true
        scope.launch {
            var wait = 30_000L
            while (true) {
                delay(wait)
                val s = _state.value
                if (!s.paired || !s.autoSync) { wait = 30_000L; continue }
                val pending = repo.pendingUploads()
                if (pending == 0L) { wait = 30_000L; continue }
                wait = if (syncNow()) 30_000L else (wait * 2).coerceAtMost(300_000L)
            }
        }
    }

    fun offer(text: String?): Boolean {
        val link = PairingLink.parse(text)
        if (link == null) { _pairMessage.value = "That is not a pairing code from the Windows app."; return false }
        _pairMessage.value = null; _pending.value = link
        return true
    }

    fun dismissPending() { _pending.value = null }
    fun clearPairMessage() { _pairMessage.value = null }

    suspend fun confirmPairing(): Boolean {
        val link = _pending.value ?: return false
        _pending.value = null
        return try {
            val r = withContext(Dispatchers.IO) { SyncClient.pair(link, phoneName(), phoneId()) }
            // Pairing with a PC this phone has not synced with before (its certificate differs): send everything to it, not just what is new.
            if (prefs.getString("lastFp", null) != link.fingerprint) withContext(Dispatchers.IO) { repo.clearUploadMarks() }
            prefs.edit().putString("lastFp", link.fingerprint).apply()
            prefs.edit().putString("token", r.token).putString("serverName", r.serverName).putString("host", link.host).putInt("port", link.port)
                .putString("fp", link.fingerprint).putString("lastMessage", "Paired. Nothing uploaded yet.").remove("lastSync").apply()
            _state.value = load()
            _pairMessage.value = "Paired with ${r.serverName}."
            scope.launch { syncNow() }
            true
        } catch (e: SyncException) {
            _pairMessage.value = when (e.kind) {
                SyncException.Kind.Refused -> "The PC refused the code: it is wrong, expired or already used. Create a new one on the PC."
                SyncException.Kind.CertificateMismatch -> "The PC's security certificate did not match the code. Pairing was stopped."
                SyncException.Kind.Unreachable -> "Could not reach the PC at ${link.host}:${link.port}. Same Wi-Fi? Is the Windows Firewall rule added? (${e.message})"
                else -> "Pairing failed: ${e.message}"
            }
            false
        }
    }

    fun unpair() {
        prefs.edit().remove("token").remove("serverName").remove("host").remove("port").remove("fp").remove("lastSync").remove("lastMessage").apply()
        _state.value = load()
        _pairMessage.value = "Unpaired. Data on this phone was kept."
    }

    fun setAutoSync(on: Boolean) { prefs.edit().putBoolean("auto", on).apply(); _state.update { it.copy(autoSync = on) } }

    /** Uploads everything not yet confirmed by the PC. Returns true when nothing is left to send. */
    suspend fun syncNow(): Boolean {
        if (!mutex.tryLock()) return false
        val s0 = _state.value
        val token = prefs.getString("token", null)
        if (token == null || s0.host.isEmpty()) { mutex.unlock(); return false }
        _state.update { it.copy(syncing = true, lastError = null) }
        try {
            var sent = 0; var stored = 0; var skew: Double? = null
            while (true) {
                val batch = repo.pendingBatch(500)
                if (batch.isEmpty() && sent > 0) break
                val body = buildBody(batch, if (sent == 0) repo.allSessions() else emptyList(), if (sent == 0) repo.recentConnectionTests() else emptyList())
                val r = withContext(Dispatchers.IO) { SyncClient.upload(s0.host, s0.port, s0.fingerprint, token, body) }
                // The PC answered 200: it stored everything it accepted. Duplicates and rejects will never become valid, so they are done too.
                if (batch.isNotEmpty()) repo.markUploaded(batch.map { it.id }, System.currentTimeMillis())
                sent += batch.size; stored += r.stored; skew = r.skewSeconds
                if (batch.size < 500) break
            }
            val msg = if (sent == 0) "Connected. Nothing new to upload." else "Uploaded $sent windows ($stored new on the PC)."
            prefs.edit().putLong("lastSync", System.currentTimeMillis()).putString("lastMessage", msg).apply()
            _state.update { it.copy(syncing = false, lastSyncMs = System.currentTimeMillis(), lastMessage = msg, lastError = null, clockSkewSeconds = skew) }
            return true
        } catch (e: SyncException) {
            if (e.kind == SyncException.Kind.Revoked) {
                prefs.edit().remove("token").apply(); _state.value = load().copy(lastError = e.message)
            } else _state.update { it.copy(syncing = false, lastError = when (e.kind) {
                SyncException.Kind.Unreachable -> "Cannot reach the PC (${e.message}). Data is kept and will be sent when it is reachable."
                SyncException.Kind.CertificateMismatch -> "The PC's certificate changed. Unpair and pair again if you trust the new one."
                else -> e.message
            }) }
            return false
        } finally {
            _state.update { it.copy(syncing = false) }
            mutex.unlock()
        }
    }

    private fun buildBody(obs: List<ObservationEntity>, sessions: List<SessionEntity>, tests: List<ConnectionTestEntity>): JSONObject {
        val o = JSONArray()
        for (x in obs) o.put(JSONObject().put("id", x.id).put("address", x.address).put("addressKind", x.addressKind).put("timestampMs", x.timestampMs)
            .put("rssi", x.rssiAvg).put("name", x.name ?: JSONObject.NULL).put("manufacturerData", x.manufacturerData ?: JSONObject.NULL)
            .put("serviceUuids", x.serviceUuids ?: JSONObject.NULL).put("advertisementData", x.advertisementData ?: JSONObject.NULL)
            .put("sessionId", x.sessionId ?: JSONObject.NULL).put("locationLabel", x.locationLabel ?: JSONObject.NULL))
        val s = JSONArray()
        for (x in sessions) s.put(JSONObject().put("id", x.id).put("locationLabel", x.locationLabel).put("startedMs", x.startedAtMs)
            .put("endedMs", x.endedAtMs ?: JSONObject.NULL).put("notes", x.notes ?: JSONObject.NULL))
        val t = JSONArray()
        for (x in tests) t.put(JSONObject().put("id", x.id.toString()).put("address", x.address).put("startedMs", x.startedMs).put("durationMs", x.durationMs)
            .put("outcome", x.outcome).put("detail", x.detail ?: JSONObject.NULL))
        return JSONObject().put("sentAtMs", System.currentTimeMillis()).put("observations", o).put("sessions", s).put("tests", t)
    }
}
