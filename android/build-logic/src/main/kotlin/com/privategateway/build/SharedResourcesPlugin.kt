package com.privategateway.build

import org.gradle.api.DefaultTask
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.api.artifacts.component.ComponentIdentifier
import org.gradle.api.artifacts.component.ModuleComponentIdentifier
import org.gradle.api.artifacts.result.ResolvedComponentResult
import org.gradle.api.artifacts.result.ResolvedDependencyResult
import org.gradle.api.file.ConfigurableFileCollection
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.provider.ListProperty
import org.gradle.api.provider.Property
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.InputDirectory
import org.gradle.api.tasks.InputFile
import org.gradle.api.tasks.InputFiles
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.PathSensitive
import org.gradle.api.tasks.PathSensitivity
import org.gradle.api.tasks.TaskAction
import java.io.File

/**
 * Задачи generatePgTheme (Kotlin: Tokens.kt и Icons.kt) и generatePgResources (strings.xml RU/EN, иконка запуска,
 * значок уведомления, список языков). Источники — те же, что у десктопа: shared/specifications/design-tokens.json,
 * shared/specifications/localization (ru.json, en.json) и контуры иконок макета из desktop/src/Client.UI/Themes/Icons.axaml.
 * generatePgLicenses — лицензии открытых компонентов в assets (shared/licenses/android.json и тексты).
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

        project.tasks.register("generatePgLicenses", GeneratePgLicenses::class.java) {
            components.set(repo.file("shared/licenses/android.json"))
            texts.set(repo.dir("shared/licenses/texts"))
            versions.from(repo.dir("android").asFileTree.matching { include("*.version") })
            appVersion.convention("")
            outputDir.set(project.layout.buildDirectory.dir("generated/pg/assets"))
        }
    }
}

/**
 * Лицензии в assets: список для экрана, тексты и THIRD-PARTY-NOTICES.txt. [modules] — «группа:имя:версия» из
 * runtime-зависимостей выпуска (приложение задает их через [MavenModules.of]), сверяются со списком android.json.
 */
abstract class GeneratePgLicenses : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val components: RegularFileProperty

    @get:InputDirectory
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val texts: DirectoryProperty

    @get:InputFiles
    @get:PathSensitive(PathSensitivity.NAME_ONLY)
    abstract val versions: ConfigurableFileCollection

    @get:Input
    abstract val modules: ListProperty<String>

    @get:Input
    abstract val appVersion: Property<String>

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun generate() {
        val files = LicensesWriter.write(
            components.get().asFile.readText(),
            texts.get().asFile.listFiles { file -> file.extension == "txt" }.orEmpty().associate { it.nameWithoutExtension to it.readText() },
            modules.get(),
            versions.files.associate { it.nameWithoutExtension to it.readText() },
            appVersion.get(),
        )
        writeAll(outputDir.get().asFile, files)
    }
}

/** Модули Maven графа зависимостей — «группа:имя:версия»; свои проекты и файлы (.aar в libs) сюда не входят. */
object MavenModules {
    fun of(root: ResolvedComponentResult): List<String> {
        val seen = HashSet<ComponentIdentifier>()
        val queue = ArrayDeque(listOf(root))
        val modules = sortedSetOf<String>()
        while (queue.isNotEmpty()) {
            val next = queue.removeFirst()
            if (!seen.add(next.id)) {
                continue
            }

            (next.id as? ModuleComponentIdentifier)?.let { modules += "${it.group}:${it.module}:${it.version}" }
            next.dependencies.filterIsInstance<ResolvedDependencyResult>().forEach { queue += it.selected }
        }

        return modules.toList()
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
