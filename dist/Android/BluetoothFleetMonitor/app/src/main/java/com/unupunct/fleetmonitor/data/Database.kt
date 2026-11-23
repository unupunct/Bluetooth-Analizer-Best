package com.unupunct.fleetmonitor.data

import androidx.room.*
import kotlinx.coroutines.flow.Flow

/** One physical advertiser as the phone knows it. Keyed by address; the server reconciles identities in Phase 3. */
@Entity(tableName = "devices", indices = [Index("lastSeenMs")])
data class DeviceEntity(
    @PrimaryKey val address: String,
    val advertisedName: String?,
    val customName: String?,
    val manufacturer: String?,
    val fingerprint: String?,
    val addressKind: String,
    val firstSeenMs: Long,
    val lastSeenMs: Long,
    val lastRssi: Int,
    val minRssi: Int,
    val maxRssi: Int,
    val rssiSum: Long,
    val observationCount: Long,
    val advertisementCount: Long,
    /** Bluetooth SIG company ids seen in this device's manufacturer data (comma separated, decimal). Schema v3. */
    val companyIds: String? = null,
    /** 16-bit service UUIDs seen (comma separated, hex). Schema v3. */
    val serviceUuids: String? = null,
    /** GAP appearance value, when the device advertises one. Schema v3. */
    val appearance: Int? = null,
    /** RSSI at 1 m measured by the user for THIS device ("calibrate"); makes distance estimates much better. Schema v3. */
    val calibRssi1m: Int? = null,
    /** Facts read from the device itself by "Connect and test". Schema v3. */
    val gattName: String? = null,
    val gattManufacturer: String? = null,
    val gattModel: String? = null,
    val gattFirmware: String? = null,
    val gattCheckedMs: Long? = null,
) {
    val displayName: String get() = customName?.takeIf { it.isNotBlank() } ?: advertisedName?.takeIf { it.isNotBlank() } ?: "Device ${address.takeLast(6)}"
    val averageRssi: Double get() = if (observationCount == 0L) 0.0 else rssiSum.toDouble() / observationCount
}

/**
 * Aggregated sampling window (see ObservationAggregator). uploadedAtMs stays null until the Windows server
 * acknowledges it (Phase 3); rows that were never acknowledged are never deleted.
 */
@Entity(
    tableName = "observations",
    indices = [Index("address", "timestampMs"), Index("timestampMs"), Index("sessionId"), Index("uploadedAtMs")],
)
data class ObservationEntity(
    @PrimaryKey val id: String,
    val address: String,
    val addressKind: String,
    val timestampMs: Long,
    val windowEndMs: Long,
    val rssi: Int,
    val rssiMin: Int,
    val rssiMax: Int,
    val rssiAvg: Double,
    val advCount: Int,
    val name: String?,
    val manufacturerData: String?,
    val serviceUuids: String?,
    val advertisementData: String?,
    val sessionId: String?,
    val locationLabel: String?,
    val scanMode: String,
    val source: String = "Android",
    val uploadedAtMs: Long? = null,
    /** Advertised transmit power in dBm, when the device includes it. Added in schema v2. */
    val txPower: Int? = null,
    /** Advertising flags byte (discoverability, BR/EDR support), when present. Added in schema v2. */
    val advertiseFlags: Int? = null,
)

/** One "Connect and test" run on one device the user chose. Never run automatically. */
@Entity(tableName = "connection_tests", indices = [Index("address")])
data class ConnectionTestEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val address: String,
    val startedMs: Long,
    val durationMs: Long,
    val outcome: String,
    val gattStatus: Int?,
    val detail: String?,
    val services: String?,
    val rssiMin: Int?,
    val rssiMax: Int?,
    val rssiAvg: Double?,
    val rssiReads: Int,
)

@Entity(tableName = "sessions")
data class SessionEntity(
    @PrimaryKey val id: String,
    val locationLabel: String,
    val startedAtMs: Long,
    val endedAtMs: Long?,
    val notes: String?,
)

@Entity(tableName = "coverage_gaps", indices = [Index("startMs")])
data class CoverageGapEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val startMs: Long,
    val endMs: Long?,
    val reason: String,
)

@Entity(tableName = "app_events", indices = [Index("timestampMs")])
data class AppEventEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val timestampMs: Long,
    val severity: String,
    val type: String,
    val message: String,
)

data class SessionSummary(val sessionId: String, val distinctDevices: Int, val observations: Long)

@Dao
interface DeviceDao {
    @Query("SELECT * FROM devices ORDER BY lastSeenMs DESC")
    fun observeAll(): Flow<List<DeviceEntity>>

    @Query("SELECT * FROM devices")
    suspend fun getAll(): List<DeviceEntity>

    @Query("SELECT * FROM devices WHERE address = :address")
    suspend fun get(address: String): DeviceEntity?

    @Upsert suspend fun upsert(devices: List<DeviceEntity>)

    @Query("UPDATE devices SET customName = :name WHERE address = :address")
    suspend fun rename(address: String, name: String?)

    @Query("UPDATE devices SET calibRssi1m = :ref WHERE address = :address")
    suspend fun setCalibration(address: String, ref: Int?)

    @Query("UPDATE devices SET gattName = :name, gattManufacturer = :maker, gattModel = :model, gattFirmware = :fw, gattCheckedMs = :at WHERE address = :address")
    suspend fun setGattInfo(address: String, name: String?, maker: String?, model: String?, fw: String?, at: Long)
}

@Dao
interface ConnectionTestDao {
    @Insert suspend fun insert(t: ConnectionTestEntity): Long
    @Query("SELECT * FROM connection_tests WHERE address = :address ORDER BY startedMs DESC LIMIT 10")
    fun observeFor(address: String): Flow<List<ConnectionTestEntity>>
}

@Dao
interface ObservationDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertAll(rows: List<ObservationEntity>): List<Long>

    @Query("SELECT * FROM observations WHERE address = :address AND timestampMs >= :fromMs ORDER BY timestampMs")
    suspend fun forDevice(address: String, fromMs: Long): List<ObservationEntity>

    @Query("SELECT COUNT(*) FROM observations")
    fun observeCount(): Flow<Long>

    @Query("SELECT COUNT(*) FROM observations WHERE uploadedAtMs IS NULL")
    fun observePending(): Flow<Long>

    @Query("SELECT sessionId, COUNT(DISTINCT address) AS distinctDevices, COUNT(*) AS observations FROM observations WHERE sessionId IS NOT NULL GROUP BY sessionId")
    fun observeSessionSummaries(): Flow<List<SessionSummary>>

    @Query("SELECT DISTINCT address FROM observations WHERE sessionId = :sessionId")
    suspend fun addressesInSession(sessionId: String): List<String>

    /** Average RSSI of this address from before the given time: the "previous readings" an inspection compares against. */
    @Query("SELECT AVG(rssiAvg) FROM observations WHERE address = :address AND timestampMs < :beforeMs")
    suspend fun averageBefore(address: String, beforeMs: Long): Double?
}

@Dao
interface SessionDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun upsert(s: SessionEntity)
    @Query("SELECT * FROM sessions ORDER BY startedAtMs DESC") fun observeAll(): Flow<List<SessionEntity>>
    @Query("SELECT * FROM sessions WHERE id = :id") suspend fun get(id: String): SessionEntity?
    @Query("SELECT * FROM sessions WHERE endedAtMs IS NULL ORDER BY startedAtMs DESC LIMIT 1") suspend fun open(): SessionEntity?
}

@Dao
interface DiagnosticsDao {
    @Insert suspend fun insertGap(g: CoverageGapEntity): Long
    @Query("UPDATE coverage_gaps SET endMs = :endMs WHERE endMs IS NULL") suspend fun closeOpenGaps(endMs: Long)
    @Query("SELECT * FROM coverage_gaps ORDER BY startMs DESC LIMIT 200") fun observeGaps(): Flow<List<CoverageGapEntity>>
    @Query("SELECT * FROM coverage_gaps ORDER BY startMs") suspend fun allGaps(): List<CoverageGapEntity>
    @Insert suspend fun insertEvent(e: AppEventEntity): Long
    @Query("SELECT * FROM app_events ORDER BY timestampMs DESC LIMIT 300") fun observeEvents(): Flow<List<AppEventEntity>>
}

@Database(
    entities = [DeviceEntity::class, ObservationEntity::class, SessionEntity::class, CoverageGapEntity::class, AppEventEntity::class, ConnectionTestEntity::class],
    version = 3,
    exportSchema = true,
)
abstract class FleetDatabase : RoomDatabase() {
    companion object {
        /** v2 -> v3: device attributes for identification, per-device calibration, GATT facts, and the connection-test log. Nothing is dropped. */
        val MIGRATION_2_3 = object : androidx.room.migration.Migration(2, 3) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                for (col in listOf("companyIds TEXT", "serviceUuids TEXT", "appearance INTEGER", "calibRssi1m INTEGER", "gattName TEXT",
                    "gattManufacturer TEXT", "gattModel TEXT", "gattFirmware TEXT", "gattCheckedMs INTEGER"))
                    db.execSQL("ALTER TABLE devices ADD COLUMN $col")
                db.execSQL("CREATE TABLE IF NOT EXISTS connection_tests (id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, address TEXT NOT NULL, startedMs INTEGER NOT NULL, durationMs INTEGER NOT NULL, outcome TEXT NOT NULL, gattStatus INTEGER, detail TEXT, services TEXT, rssiMin INTEGER, rssiMax INTEGER, rssiAvg REAL, rssiReads INTEGER NOT NULL)")
                db.execSQL("CREATE INDEX IF NOT EXISTS index_connection_tests_address ON connection_tests (address)")
            }
        }

        /** v1 -> v2: keep every stored observation, add the two optional advertisement fields. */
        val MIGRATION_1_2 = object : androidx.room.migration.Migration(1, 2) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE observations ADD COLUMN txPower INTEGER")
                db.execSQL("ALTER TABLE observations ADD COLUMN advertiseFlags INTEGER")
            }
        }
    }

    abstract fun devices(): DeviceDao
    abstract fun observations(): ObservationDao
    abstract fun sessions(): SessionDao
    abstract fun diagnostics(): DiagnosticsDao
    abstract fun connectionTests(): ConnectionTestDao
}
