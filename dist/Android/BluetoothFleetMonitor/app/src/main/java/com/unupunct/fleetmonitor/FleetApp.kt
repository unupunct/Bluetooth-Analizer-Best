package com.unupunct.fleetmonitor

import android.app.Application
import androidx.room.Room
import com.unupunct.fleetmonitor.data.FleetDatabase
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.HiltAndroidApp
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@HiltAndroidApp
class FleetApp : Application() {
    @javax.inject.Inject lateinit var sync: com.unupunct.fleetmonitor.sync.SyncManager

    override fun onCreate() {
        super.onCreate()
        sync.start()   // uploads to a paired PC in the background while the process is alive (a running scan keeps it alive)
    }
}

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides
    @Singleton
    fun database(@ApplicationContext context: android.content.Context): FleetDatabase =
        Room.databaseBuilder(context, FleetDatabase::class.java, "fleet.db").addMigrations(FleetDatabase.MIGRATION_1_2, FleetDatabase.MIGRATION_2_3).build()
}
