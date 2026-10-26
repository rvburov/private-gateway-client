package com.privategateway.build

import org.gradle.api.DefaultTask
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.tasks.InputFile
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction
import java.io.File

/**
 * Задачи generatePgTheme (Kotlin: Tokens.kt и Icons.kt) и generatePgResources (strings.xml RU/EN, иконка запуска,
 * значок уведомления, список языков). Источники — те же, что у десктопа: shared/specifications/design-tokens.json,
 * shared/specifications/localization (ru.json, en.json) и контуры иконок макета из desktop/src/Client.UI/Themes/Icons.axaml.
 * Папки результата подключает к вариантам сборки приложение (android/app/build.gradle.kts).
 */
class SharedResourcesPlugin : Plugin<Project> {
    override fun apply(project: Project) {
        val repo = project.rootProject.layout.projectDirectory.dir("..")
        val specifications = repo.dir("shared/specifications")
        project.tasks.register("generatePgTheme", GeneratePgTheme::class.java) {
            tokens.set(specifications.file("design-tokens.json"))
            icons.set(repo.file("desktop/src/Client.UI/Themes/Icons.axaml"))
            outputDir.set(project.layout.buildDirectory.dir("generated/pg/kotlin"))
        }

        project.tasks.register("generatePgResources", GeneratePgResources::class.java) {
            tokens.set(specifications.file("design-tokens.json"))
            icons.set(repo.file("desktop/src/Client.UI/Themes/Icons.axaml"))
            russian.set(specifications.file("localization/ru.json"))
            english.set(specifications.file("localization/en.json"))
            outputDir.set(project.layout.buildDirectory.dir("generated/pg/res"))
        }
    }
}

/** Kotlin-файлы темы: путь внутри папки исходников → текст. */
abstract class GeneratePgTheme : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val tokens: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val icons: RegularFileProperty

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun generate() {
        val files = ThemeWriter.write(tokens.get().asFile.readText(), icons.get().asFile.readText())
        writeAll(outputDir.get().asFile, files)
    }
}

/** Ресурсы Android: путь внутри папки res → текст. */
abstract class GeneratePgResources : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val tokens: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val icons: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val russian: RegularFileProperty

    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val english: RegularFileProperty

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun generate() {
        val files = ResourcesWriter.write(
            tokens.get().asFile.readText(),
            icons.get().asFile.readText(),
            russian.get().asFile.readText(),
            english.get().asFile.readText(),
        )
        writeAll(outputDir.get().asFile, files)
    }
}

/** Папка результата — заново: файл, которого больше нет в источнике, не должен остаться от прошлой сборки. */
private fun writeAll(directory: File, files: Map<String, String>) {
    directory.deleteRecursively()
    for ((path, text) in files) {
        val file = File(directory, path)
        file.parentFile.mkdirs()
        file.writeText(text)
    }
}
