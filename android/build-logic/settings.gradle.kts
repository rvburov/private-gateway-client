// Плагин сборки Android: тема, иконки и тексты приложения собираются из общих файлов shared/specifications при каждой
// сборке — как Tokens.axaml у десктопа, только без сгенерированного файла в git. Отдельная сборка: собирается и
// тестируется без SDK Android и без репозитория Google.
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

rootProject.name = "build-logic"
