// Kotlin компилирует сам AGP 9 (встроенный Kotlin) — своим KGP не ниже 2.2.10; версия Kotlin проекта задается здесь,
// в classpath корневой сборки (решение 447).
buildscript {
    repositories {
        mavenCentral()
    }
    dependencies {
        classpath(libs.kotlin.gradle.plugin)
    }
}

// Плагины объявлены здесь один раз, применяются в модулях.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.ksp) apply false
    alias(libs.plugins.hilt) apply false
}
