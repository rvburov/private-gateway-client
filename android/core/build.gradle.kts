import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Общая логика Android: разбор ссылок и подписок, описание сервера, конфиг ядра — без Android API.
plugins {
    alias(libs.plugins.kotlin.jvm)
}

group = "com.privategateway"
version = "0.1.0"

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions {
        jvmTarget = JvmTarget.JVM_17
        allWarningsAsErrors = true
    }
}

dependencies {
    implementation(libs.snakeyaml.engine)
    implementation(libs.zxing.core)
    // Наблюдение за связью и переподключение (TunnelGuard) — ожидания и сигналы на корутинах, как в приложении.
    implementation(libs.kotlinx.coroutines.core)
    testImplementation(platform(libs.junit.bom))
    testImplementation(libs.junit.jupiter)
    testImplementation(libs.junit.params)
    testImplementation(libs.kotlinx.serialization.json)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(kotlin("test"))
    testRuntimeOnly(libs.junit.launcher)
}

tasks.test {
    useJUnitPlatform()
    // Эталоны разбора — общие для десктопа, Android и iOS.
    systemProperty("testdata", rootDir.resolve("../../shared/testdata").canonicalPath)
    // Xray-core той версии, что в libXray (scripts/get-xray-mobile.sh): конфиги ядра читает само ядро (ReferenceConfigTest).
    val exe = if (System.getProperty("os.name").startsWith("Windows")) ".exe" else ""
    val xrayMobile = rootDir.resolve("../../artifacts/cores/xray-mobile/xray$exe")
    systemProperty("xrayMobile", xrayMobile.canonicalPath)
    // Ядро и флаг CI — входы задачи: пересобранное или появившееся ядро и PG_REQUIRE_XRAY_MOBILE запускают тесты заново.
    inputs.files(xrayMobile).withPropertyName("xrayMobile").withPathSensitivity(org.gradle.api.tasks.PathSensitivity.NONE)
    inputs.property("requireXrayMobile", providers.environmentVariable("PG_REQUIRE_XRAY_MOBILE").orElse(""))
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
