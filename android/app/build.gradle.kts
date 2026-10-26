import com.privategateway.build.GeneratePgResources
import com.privategateway.build.GeneratePgTheme
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Приложение Android: Compose, VpnService и ядро Xray (libXray, .aar от gomobile — собирает scripts/get-libxray.sh).
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
    id("privategateway.shared-resources")
}

android {
    namespace = "com.privategateway.client"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.privategateway.client"
        minSdk = 28
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
        // Проверки на эмуляторе и устройстве (androidTest): настоящий туннель — пункт 9.4.
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            // Подпись выпуска — ключом пользователя, когда будет выпуск (README, этап 11); сжатие — вместе с ней.
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    lint {
        abortOnError = true
        checkDependencies = true
        // Тексты общие для всех приложений: многие нужны только десктопу — неиспользуемая строка здесь не ошибка.
        disable += "UnusedResources"
        // Версии библиотек обновляются отдельным пунктом, а не потому, что вышла новая: проверка зависит от даты.
        disable += listOf("GradleDependency", "NewerVersionAvailable", "AndroidGradlePluginVersion", "OldTargetApi")
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            all {
                it.systemProperty("testdata", rootDir.resolve("../shared/testdata").canonicalPath)
                it.testLogging {
                    events("failed")
                    exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
                }
            }
        }
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = JvmTarget.JVM_17
        allWarningsAsErrors = true
    }
}

// Тема (Tokens.kt, Icons.kt) и ресурсы (тексты RU/EN, иконка запуска) — из shared/specifications при каждой сборке.
// Ресурсы — через API вариантов AGP; Kotlin-плагин сгенерированные папки этого API не видит (AGP 8.13, Kotlin 2.2),
// поэтому тема — папкой исходников main, а генерация — до любой сборки (preBuild).
val pgTheme = tasks.named<GeneratePgTheme>("generatePgTheme")
val pgResources = tasks.named<GeneratePgResources>("generatePgResources")
android.sourceSets.getByName("main").java.srcDir(pgTheme.flatMap { it.outputDir })
tasks.configureEach {
    // Все, кто читает исходники (KSP, компиляция, Lint), — после генерации темы.
    if (name == "preBuild" || name.startsWith("ksp") || name.startsWith("compile") || name.contains("Lint")) {
        dependsOn(pgTheme)
    }
}
androidComponents {
    onVariants { variant ->
        variant.sources.res?.addGeneratedSourceDirectory(pgResources, GeneratePgResources::outputDir)
    }
}

// Схемы базы (Room) — в app/schemas, в git: по ним MigrationTestHelper проверяет миграции версий 2 и дальше.
ksp {
    arg("room.schemaLocation", "$projectDir/schemas")
}

dependencies {
    implementation("com.privategateway:core")
    implementation(files("libs/libXray.aar"))

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.activity.compose)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.lifecycle.runtime.compose)
    implementation(libs.lifecycle.service)
    implementation(libs.core.ktx)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.hilt.android)
    implementation(libs.hilt.navigation.compose)
    ksp(libs.hilt.compiler)
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.datastore.preferences)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)

    testImplementation(libs.junit4)
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(libs.androidx.test.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(platform(libs.compose.bom))
    testImplementation(libs.compose.ui.test.junit4)
    testImplementation(kotlin("test-junit"))

    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(kotlin("test-junit"))
}
