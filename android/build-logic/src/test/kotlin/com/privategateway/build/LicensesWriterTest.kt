package com.privategateway.build

import groovy.json.JsonSlurper
import org.gradle.api.GradleException
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class LicensesWriterTest {
    private val repo = File(System.getProperty("repo"))
    private val json = repo.resolve("shared/licenses/android.json").readText()
    private val texts = repo.resolve("shared/licenses/texts").listFiles { file -> file.extension == "txt" }!!.associate { it.nameWithoutExtension to it.readText() }
    private val versions = mapOf("libxray" to repo.resolve("android/libxray.version").readText())

    /** По модулю на каждый шаблон настоящего списка: «androidx.*» → «androidx.x:x:1.0», «com.google.zxing:*» → «com.google.zxing:x:1.0». */
    private val modules = LicensesWriter.parse(json).flatMap { it.maven }.map { pattern ->
        val key = if (pattern.endsWith("*")) pattern.dropLast(1).let { if (it.contains(':')) it + "x" else it + "x:x" } else pattern
        "$key:1.0"
    }

    private fun write(modules: List<String> = this.modules, list: String = json, licenses: Map<String, String> = texts, aar: Map<String, String> = versions) =
        LicensesWriter.write(list, licenses, modules, aar, "0.1.0")

    @Suppress("UNCHECKED_CAST")
    private fun entries(files: Map<String, String>) = JsonSlurper().parseText(files.getValue(LicensesWriter.LIST)) as List<Map<String, Any>>

    @Test
    fun `the real list covers every pattern and carries the texts it needs`() {
        val files = write()
        val entries = entries(files)
        assertEquals(LicensesWriter.parse(json).map { it.name }, entries.map { it["name"] })
        val xray = entries.single { it["name"] == "Xray-core" }
        assertEquals("MPL-2.0", xray["license"])
        assertEquals("libXray", xray["inside"])
        val libxray = entries.single { it["name"] == "libXray" }
        val commit = versions.getValue("libxray").trim()
        assertEquals(commit, libxray["version"])
        assertEquals("https://github.com/XTLS/libXray/tree/$commit", libxray["source"])
        for (id in entries.flatMap { it["texts"] as List<*> }.toSet()) {
            assertEquals(texts.getValue(id as String).trimEnd() + "\n", files.getValue("licenses/$id.txt"))
        }

        assertEquals(setOf(LicensesWriter.LIST, LicensesWriter.NOTICES, "licenses/MIT.txt", "licenses/MPL-2.0.txt", "licenses/Apache-2.0.txt"), files.keys)
        val notices = files.getValue(LicensesWriter.NOTICES)
        assertTrue(notices.startsWith("Private Gateway Client 0.1.0 (Android)\n"))
        for (entry in entries) {
            assertTrue(notices.lines().any { it == entry["name"] || it.startsWith("${entry["name"]} ") }, "${entry["name"]} есть в THIRD-PARTY-NOTICES")
        }

        assertTrue(notices.contains("Исходный код / Source code: https://github.com/XTLS/libXray/tree/$commit"))
        assertTrue(notices.contains("Входит в / Part of: libXray"))
        assertTrue(notices.contains("---------------------------------------- MPL-2.0\nMozilla Public License Version 2.0"))
    }

    @Test
    fun `a module goes to the most specific component`() {
        val files = write(modules + listOf("androidx.compose.ui:ui:1.9.0", "androidx.core:core:1.17.0", "androidx.work:work-runtime:2.10.0"))
        val owners = entries(files).associate { it["name"] to it["modules"] as List<*> }
        assertTrue("androidx.compose.ui:ui 1.9.0" in owners.getValue("Jetpack Compose"))
        assertTrue("androidx.core:core 1.17.0" in owners.getValue("AndroidX"))
        assertTrue("androidx.work:work-runtime 2.10.0" in owners.getValue("WorkManager"))
        assertEquals(1, owners.values.count { "androidx.compose.ui:ui 1.9.0" in it })
        assertTrue(files.getValue(LicensesWriter.NOTICES).contains("androidx.core:core 1.17.0"))
    }

    @Test
    fun `patterns match whole names or a tail after the star`() {
        assertTrue(LicensesWriter.covers("org.jetbrains:annotations", "org.jetbrains:annotations"))
        assertTrue(!LicensesWriter.covers("org.jetbrains:annotations", "org.jetbrains:annotations-java5"))
        assertTrue(LicensesWriter.covers("androidx.*", "androidx.core:core"))
        assertTrue(!LicensesWriter.covers("androidx.*", "android.arch.core:runtime"))
        assertEquals(listOf("MIT", "Apache-2.0"), LicensesWriter.textIds("(MIT OR Apache-2.0)"))
        assertEquals(listOf("GPL-3.0"), LicensesWriter.textIds("GPL-3.0-or-later"))
    }

    @Test
    fun `a module nobody covers stops the build and is named`() {
        val error = assertFailsWith<GradleException> { write(modules + "org.example:unknown:2.0") }
        assertTrue(error.message!!.contains("org.example:unknown:2.0"), error.message)
    }

    @Test
    fun `a component left without modules is a stale line`() {
        val error = assertFailsWith<GradleException> { write(modules.filterNot { it.startsWith("com.google.zxing:") }) }
        assertTrue(error.message!!.contains("«ZXing»"), error.message)
    }

    @Test
    fun `a license without its text or an aar without a version stops the build`() {
        val noText = assertFailsWith<GradleException> { write(licenses = texts - "MPL-2.0") }
        assertTrue(noText.message!!.contains("MPL-2.0"), noText.message)
        val noVersion = assertFailsWith<GradleException> { write(aar = emptyMap()) }
        assertTrue(noVersion.message!!.contains("android/libxray.version"), noVersion.message)
    }

    @Test
    fun `a component comes exactly one way and inside names a known one`() {
        val both = """[{"name": "A", "license": "MIT", "copyright": "c", "url": "u", "maven": ["a:*"], "aar": "a"}]"""
        val twice = assertFailsWith<GradleException> { write(listOf("a:a:1"), both, aar = mapOf("a" to "1")) }
        assertTrue(twice.message!!.contains("одним путем"), twice.message)
        val unknown = """
            // комментарий
            [{"name": "A", "license": "MIT", "copyright": "c", "url": "u", "inside": "B"}]
        """.trimIndent()
        val error = assertFailsWith<GradleException> { write(emptyList(), unknown) }
        assertTrue(error.message!!.contains("«B»"), error.message)
        val noUrl = """[{"name": "A", "license": "MIT", "copyright": "c", "maven": ["a:*"]}]"""
        assertFailsWith<GradleException> { write(listOf("a:a:1"), noUrl) }
    }
}
