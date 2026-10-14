import java.util.Properties

plugins {
    id("com.android.application")
}

// Optional publisher key: when android/keystore.properties exists, builds are signed with it
// instead of this machine's Android debug key. The file and the keystore stay out of source control.
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use(::load)
}

android {
    signingConfigs {
        if (keystoreProperties.isNotEmpty()) create("publisher") {
            storeFile = rootProject.file(keystoreProperties.getProperty("storeFile"))
            storePassword = keystoreProperties.getProperty("storePassword")
            keyAlias = keystoreProperties.getProperty("keyAlias")
            keyPassword = keystoreProperties.getProperty("keyPassword")
        }
    }

    namespace = "lan.asta.freightcopilot"
    compileSdk = 36

    defaultConfig {
        applicationId = "lan.asta.freightcopilot"
        minSdk = 26
        targetSdk = 36
        versionCode = 13
        versionName = "0.8.0"
    }

    buildTypes {
        debug {
            signingConfigs.findByName("publisher")?.let { signingConfig = it }
        }
        release {
            signingConfigs.findByName("publisher")?.let { signingConfig = it }
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    // Camera QR scanning for PC linking; works without Google Play services.
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    // The scanner calls ContextCompat for the camera permission but does not bring androidx.core along.
    implementation("androidx.core:core:1.13.1")
}

