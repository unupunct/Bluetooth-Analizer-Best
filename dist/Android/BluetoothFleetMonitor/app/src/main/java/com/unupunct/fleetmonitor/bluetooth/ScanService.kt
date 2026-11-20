package com.unupunct.fleetmonitor.bluetooth

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.le.ScanResult
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.core.content.IntentCompat
import com.unupunct.fleetmonitor.MainActivity
import com.unupunct.fleetmonitor.data.ScanMode
import com.unupunct.fleetmonitor.domain.ScannerState
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.AndroidEntryPoint
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.collectLatest
import javax.inject.Inject

/**
 * Foreground service that keeps Continuous and Inspection scans alive while the screen is off.
 * The notification is always visible while scanning; Stop is one tap away.
 */
@AndroidEntryPoint
class ScanService : Service() {
    @Inject lateinit var engine: ScanEngine
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var watcher: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        createChannel()
        if (intent?.action == ACTION_STOP) {
            engine.stop(byUser = true)
            stopForegroundAndSelf()
            return START_NOT_STICKY
        }
        val mode = runCatching { ScanMode.valueOf(intent?.getStringExtra(EXTRA_MODE) ?: "") }.getOrNull() ?: ScanMode.Continuous
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification("Starting scan..."), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        val error = engine.start(mode)
        if (error != null && engine.status.value.state != ScannerState.Running) {
            // Could not start at all: leave a notification-free exit rather than a fake "scanning" notice.
            stopForegroundAndSelf()
            return START_NOT_STICKY
        }
        watcher?.cancel()
        watcher = scope.launch {
            engine.status.collectLatest { s ->
                val text = when (s.state) {
                    ScannerState.Running -> "Scanning (${engine.mode.value.label}) - ${engine.counters.value.heard} advertisements heard"
                    ScannerState.RunningNoAdvertisements -> "Scanning, but nothing heard yet"
                    ScannerState.Interrupted -> "Interrupted: ${s.message}"
                    else -> s.message
                }
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification(text))
                if (s.state == ScannerState.Stopped) stopForegroundAndSelf()
            }
        }
        return START_NOT_STICKY
    }

    private fun stopForegroundAndSelf() {
        watcher?.cancel()
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onTaskRemoved(rootIntent: Intent?) { /* the scan continues; the notification stays visible */ }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, ScanService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("Bluetooth Fleet Monitor")
            .setContentText(text)
            .setOngoing(true).setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(0, "Stop scanning", stop)
            .build()
    }

    private fun createChannel() {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL_ID) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Scanning", NotificationManager.IMPORTANCE_LOW).apply { description = "Shown while the phone is scanning for Bluetooth devices" })
    }

    companion object {
        private const val CHANNEL_ID = "scan"
        private const val NOTIFICATION_ID = 7
        const val ACTION_STOP = "com.unupunct.fleetmonitor.STOP"
        const val EXTRA_MODE = "mode"

        fun start(context: Context, mode: ScanMode) {
            ContextCompat.startForegroundService(context, Intent(context, ScanService::class.java).putExtra(EXTRA_MODE, mode.name))
        }

        fun stop(context: Context) {
            context.startService(Intent(context, ScanService::class.java).setAction(ACTION_STOP))
        }
    }
}

@EntryPoint
@InstallIn(SingletonComponent::class)
interface ScanEngineEntryPoint { fun engine(): ScanEngine }

/** Receives results from the OS-managed (PendingIntent) scan used by Background mode, even when the app process was not running. */
class BackgroundScanReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION) return
        val results = IntentCompat.getParcelableArrayListExtra(intent, android.bluetooth.le.BluetoothLeScanner.EXTRA_LIST_SCAN_RESULT, ScanResult::class.java)
        if (results.isNullOrEmpty()) return
        EntryPointAccessors.fromApplication(context.applicationContext, ScanEngineEntryPoint::class.java).engine().ingestBackground(results)
    }

    companion object { const val ACTION = "com.unupunct.fleetmonitor.BACKGROUND_SCAN_RESULT" }
}
