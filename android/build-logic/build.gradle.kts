plugins {
    `kotlin-dsl`
}

kotlin {
    compilerOptions {
        allWarningsAsErrors = true
    }
}

gradlePlugin {
    plugins {
        register("sharedResources") {
            id = "privategateway.shared-resources"
            implementationClass = "com.privategateway.build.SharedResourcesPlugin"
        }
    }
}

dependencies {
    testImplementation(platform(libs.junit.bom))
    testImplementation(libs.junit.jupiter)
    testImplementation(kotlin("test"))
    testRuntimeOnly(libs.junit.launcher)
}

tasks.test {
    useJUnitPlatform()
    // Генераторы проверяются на настоящих файлах репозитория: токенах, иконках и текстах RU/EN.
    systemProperty("repo", rootDir.resolve("../..").canonicalPath)
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
