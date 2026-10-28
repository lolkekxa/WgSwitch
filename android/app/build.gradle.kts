plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

// версия как у Windows-сборки: в CI приходит снаружи (-PappVersion=1.0.3), локально — из WgSwitch.csproj
val appVersion: String = (findProperty("appVersion") as String?)
    ?: Regex("<Version>([^<]+)</Version>").find(rootDir.resolve("../WgSwitch.csproj").readText())!!.groupValues[1]
val versionNumbers = appVersion.substringBefore('-').split('.').map { it.toInt() }

android {
    namespace = "ru.lkxa.wgswitch"
    compileSdk = 37

    defaultConfig {
        applicationId = "ru.lkxa.wgswitch"
        minSdk = 26
        targetSdk = 37
        versionName = appVersion
        versionCode = versionNumbers[0] * 10000 + versionNumbers[1] * 100 + versionNumbers[2]
    }

    // ключ подписи — из переменных окружения (CI); без них release подписывается отладочным ключом
    val keystore = System.getenv("WGS_KEYSTORE")
    signingConfigs {
        if (keystore != null) create("release") {
            storeFile = file(keystore)
            storePassword = System.getenv("WGS_KEYSTORE_PASSWORD")
            keyAlias = System.getenv("WGS_KEY_ALIAS")
            keyPassword = System.getenv("WGS_KEY_PASSWORD")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (keystore != null) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        jniLibs.useLegacyPackaging = false
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2026.09.00")
    implementation(composeBom)
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.animation:animation")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.compose.ui:ui-tooling-preview")

    implementation("androidx.core:core-ktx:1.19.1")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.11.0")

    // движок AmneziaWG (WireGuard с маскировкой от DPI; конфиги без Jc/S1/H1 работают как обычный WireGuard).
    // Собран из amnezia-vpn/amneziawg-android v3.1.20260814 (Apache-2.0) скриптом tools/build-awg.sh
    implementation(files("libs/amneziawg-tunnel-3.1.20260814.aar"))
    implementation("androidx.annotation:annotation:1.9.1")
    implementation("androidx.collection:collection:1.5.0")
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
    // сервисы Google тянут Fragment 1.0, где ActivityResult работает с ошибками
    implementation("androidx.fragment:fragment:1.9.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
