// Independent instrumentation container: no app compile dependency, no app keep-rule changes.
// The installed target remains the fully optimized production APK.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.aegis.node.frameworktest"
    compileSdk = 37
    defaultConfig {
        applicationId = "com.aegis.node.test"
        minSdk = 26
        targetSdk = 37
        versionCode = 1
        versionName = "fixture"
    }
    signingConfigs {
        create("fixtureRelease") {
            System.getenv("ANDROID_KEYSTORE_PATH")?.let { storeFile = file(it) }
            storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
            keyAlias = System.getenv("ANDROID_KEY_ALIAS")
            keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
        }
    }
    buildTypes {
        getByName("release") {
            signingConfig = signingConfigs.getByName("fixtureRelease")
            optimization { enable = false }
        }
    }
    sourceSets.getByName("main").java.srcDir("shared")
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_1_8
        targetCompatibility = JavaVersion.VERSION_1_8
    }
}
kotlin { compilerOptions { jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_1_8 } }
dependencies {
    implementation("androidx.test.ext:junit:1.3.0")
    implementation("androidx.test:runner:1.7.0")
    implementation("androidx.test:core:1.7.0")
    implementation("androidx.test.espresso:espresso-core:3.5.0")
}
