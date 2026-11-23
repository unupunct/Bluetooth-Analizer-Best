import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
}

android {
    namespace = "com.unupunct.fleetmonitor"
    compileSdk = 35
    buildToolsVersion = "35.0.0"

    defaultConfig {
        applicationId = "com.unupunct.fleetmonitor"
        minSdk = 29          // Android 10
        targetSdk = 35
        versionCode = 6
        versionName = "0.4.0"
    }

    // Release signing key: ../Signing/keystore.properties (git-ignored, never committed). Without it the release build falls back to the
    // debug key so the project still builds on a fresh checkout, but that APK is NOT the one to hand out.
    val signingProps = Properties().apply {
        val f = rootProject.file("../Signing/keystore.properties")
        if (f.exists()) f.inputStream().use { load(it) }
    }
    val hasReleaseKey = signingProps.getProperty("storeFile") != null
    signingConfigs {
        if (hasReleaseKey) create("release") {
            storeFile = rootProject.file("../" + signingProps.getProperty("storeFile"))
            storePassword = signingProps.getProperty("storePassword")
            keyAlias = signingProps.getProperty("keyAlias")
            keyPassword = signingProps.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false            // R8 off on purpose: Hilt/Room/Compose reflection needs a proven rule set before turning it on
            isDebuggable = false
            signingConfig = if (hasReleaseKey) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true }
    testOptions { unitTests.isReturnDefaultValues = true }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
}

ksp {
    arg("room.schemaLocation", "$projectDir/schemas")
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons)
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.hilt.android)
    ksp(libs.hilt.compiler)
    implementation(libs.hilt.navigation.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.zxing.embedded)   // offline QR scanner (Apache 2.0); no Google services, no network

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
}

// Unit tests are small; a modest heap keeps them runnable on machines with little free commit memory.
tasks.withType<Test>().configureEach { maxHeapSize = "256m"; jvmArgs("-Xms32m") }
