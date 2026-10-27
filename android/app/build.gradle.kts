import com.privategateway.build.GeneratePgLicenses
import com.privategateway.build.GeneratePgResources
import com.privategateway.build.MavenModules
import com.privategateway.build.GeneratePgTheme
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Приложение Android: Compose, VpnService и ядро Xray (libXray, .aar от gomobile — собирает scripts/get-libxray.sh).
plugins {
    alias(libs.plugins.android.application)
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

// Тема (Tokens.kt, Icons.kt) и ресурсы (тексты RU/EN, иконка запуска) — из shared/specifications при каждой сборке,
// через API вариантов AGP: встроенный Kotlin AGP 9 видит сгенерированные папки сам. KSP и Lint читают исходники до
// компиляции — им генерация темы до начала работы. Лицензии открытых компонентов (assets) — из shared/licenses со
// сверкой с runtime-зависимостями выпуска: модуль без строки в android.json не дает собрать приложение (решение 449).
val pgTheme = tasks.named<GeneratePgTheme>("generatePgTheme")
val pgResources = tasks.named<GeneratePgResources>("generatePgResources")
val pgLicenses = tasks.named<GeneratePgLicenses>("generatePgLicenses") {
    appVersion.set(android.defaultConfig.versionName.orEmpty())
}
configurations.matching { it.name == "releaseRuntimeClasspath" }.configureEach {
    val graph = incoming.resolutionResult.rootComponent
    pgLicenses.configure { modules.set(graph.map { MavenModules.of(it) }) }
}
tasks.configureEach {
    if (name == "preBuild" || name.startsWith("ksp") || name.contains("Lint")) {
        dependsOn(pgTheme)
    }
}
androidComponents {
    onVariants { variant ->
        variant.sources.kotlin?.addGeneratedSourceDirectory(pgTheme, GeneratePgTheme::outputDir)
        variant.sources.res?.addGeneratedSourceDirectory(pgResources, GeneratePgResources::outputDir)
        variant.sources.assets?.addGeneratedSourceDirectory(pgLicenses, GeneratePgLicenses::outputDir)
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
    implementation(libs.work.runtime)
    implementation(libs.camera.camera2)
    implementation(libs.camera.lifecycle)
    implementation(libs.camera.view)
    debugImplementation(libs.compose.ui.tooling)
    debugImplementation(libs.compose.ui.test.manifest)

    testImplementation(libs.junit4)
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(libs.androidx.test.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.work.testing)
    testImplementation(platform(libs.compose.bom))
    testImplementation(libs.compose.ui.test.junit4)
    testImplementation(kotlin("test-junit"))

    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(kotlin("test-junit"))
}
