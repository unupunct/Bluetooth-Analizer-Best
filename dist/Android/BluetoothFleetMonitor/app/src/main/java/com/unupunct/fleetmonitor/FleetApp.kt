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
class FleetApp : Application()

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides
    @Singleton
    fun database(@ApplicationContext context: android.content.Context): FleetDatabase =
        Room.databaseBuilder(context, FleetDatabase::class.java, "fleet.db").build()
}
