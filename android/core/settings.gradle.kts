// Общий модуль Android на чистом Kotlin: отдельная сборка — собирается и тестируется без SDK Android и без репозитория
// Google (облачная машина и любой компьютер с JDK), приложение подключает ее составной сборкой.
pluginManagement {
    repositories {
        gradlePluginPortal()
        mavenCentral()
    }
}

dependencyResolutionManagement {
    repositories {
        mavenCentral()
    }
    versionCatalogs {
        create("libs") {
            from(files("../gradle/libs.versions.toml"))
        }
    }
}

rootProject.name = "core"
