package com.privategateway.client.core.countries

import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import org.junit.jupiter.params.provider.ValueSource
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.File
import java.io.IOException
import java.nio.ByteBuffer
import java.util.zip.GZIPInputStream
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Геобаза PGCTRY1 — те же случаи, что у десктопа (CountryDatabaseTests), плюс отображение файла в память. */
class CountryDatabaseTest {
    @ParameterizedTest
    @CsvSource(
        "192.0.2.0,", "192.0.2.1,JP", "192.0.2.9,JP", "192.0.2.10,", "198.51.100.1,NL", "198.51.100.255,NL", "203.0.113.1,",
        "::ffff:192.0.2.1,JP", "2001:db8::1,DE", "2001:db8::ffff,DE", "2001:db8::1:0,", "2001:db8::1%3,",
    )
    fun finds_only_the_containing_range_including_both_edges(ip: String, country: String?) {
        val database = CountryDatabase.read(ByteArrayInputStream(ranges()))
        assertEquals(3, database.count)
        assertEquals(country, database.find(IpLiteral.parse(ip)!!))
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "example.com", "127.0.0.1", "10.1.2.3", "192.0.2.1", "198.51.100.10", "203.0.113.1", "2001:db8::1", "::ffff:192.0.2.1",
            "fe80::1%4", "fc00::1", "::1", "ff02::1", "", "1.2.3", "256.1.1.1", "1:2:3", "[2001:db8::1]",
        ],
    )
    fun shipped_database_never_assigns_a_country_to_special_addresses_or_names(value: String) {
        assertNull(shipped().lookup(value))
    }

    @Test
    fun shipped_database_is_valid_has_the_expected_scale_and_knows_public_addresses() {
        val database = shipped()
        assertTrue(database.count > 600_000)
        // Адрес сервера Германии из отчета владельца 2026-11-19 (десктоп показывает флаг DE) и общеизвестный DNS.
        assertEquals("DE", database.lookup("187.124.2.129"))
        assertEquals("FI", database.lookup("80.47.227.44"))
        assertEquals("FI", database.lookup("[80.47.227.44]"))
    }

    @Test
    fun mapped_file_answers_like_the_stream(@TempDir folder: File) {
        val file = File(folder, "country.bin").apply { writeBytes(ranges()) }
        val database = CountryDatabase.open(file)
        assertEquals("NL", database.find(IpLiteral.parse("198.51.100.7")!!))
        assertEquals("DE", database.find(IpLiteral.parse("2001:db8::2")!!))

        file.writeBytes(ranges() + 0)
        assertFailsWith<IOException> { CountryDatabase.open(file) }
        file.writeBytes(ranges().copyOf(ranges().size - 1))
        assertFailsWith<EOFException> { CountryDatabase.open(file) }
    }

    @ParameterizedTest
    @ValueSource(ints = [0, 8, 44])
    fun rejects_corrupt_format_count_or_country(offset: Int) {
        val bytes = ranges().also { it[offset] = 255.toByte() }
        assertFailsWith<IOException> { CountryDatabase.read(ByteArrayInputStream(bytes)) }
    }

    @Test
    fun rejects_overlapping_ranges_and_truncation() {
        val bytes = ranges()
        bytes.copyInto(bytes, 12 + 34, 12, 12 + 32)
        assertFailsWith<IOException> { CountryDatabase.read(ByteArrayInputStream(bytes)) }
        assertFailsWith<EOFException> { CountryDatabase.read(ByteArrayInputStream(ranges().copyOf(ranges().size - 1))) }
        assertFailsWith<IOException> { CountryDatabase.read(ByteArrayInputStream(ranges() + 0)) }
    }

    @ParameterizedTest
    @CsvSource(
        "1.2.3.4,true", "8.8.8.8,true", "100.63.0.1,true", "100.64.0.1,false", "172.32.0.1,true", "172.31.0.1,false",
        "2a00:1450::1,true", "2001:db8::1,false", "2001:0:1::1,false", "3fff::1,false", "fe80::1,false", "::ffff:8.8.8.8,true",
    )
    fun public_addresses_match_the_desktop_rules(ip: String, public: Boolean) {
        assertEquals(public, IpLiteral.isPublic(IpLiteral.parse(ip)!!))
    }

    @ParameterizedTest
    @ValueSource(strings = ["example.com", "1.2.3.4.5", "01.2.3.4x", ":::", "1::2::3", "12345::1", "g::1", "1:2:3:4:5:6:7:8:9", "::1.2.3"])
    fun names_and_broken_addresses_are_not_literals(value: String) {
        assertNull(IpLiteral.parse(value))
    }

    private fun shipped(): CountryDatabase {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "shared/countries/country.bin.gz").exists()) {
            dir = dir.parentFile
        }

        return GZIPInputStream(File(dir!!, "shared/countries/country.bin.gz").inputStream()).use { CountryDatabase.read(it) }
    }

    private fun ranges(): ByteArray {
        val out = ByteArrayOutputStream()
        out.write("PGCTRY1\u0000".toByteArray(Charsets.US_ASCII))
        out.write(ByteBuffer.allocate(4).putInt(3).array())
        for ((start, end, code) in listOf(Triple("192.0.2.1", "192.0.2.9", "JP"), Triple("198.51.100.1", "198.51.100.255", "NL"), Triple("2001:db8::1", "2001:db8::ffff", "DE"))) {
            out.write(v6(start))
            out.write(v6(end))
            out.write(code.toByteArray(Charsets.US_ASCII))
        }

        return out.toByteArray()
    }

    private fun v6(ip: String): ByteArray {
        val bytes = IpLiteral.parse(ip)!!.bytes
        return if (bytes.size == 16) bytes else ByteArray(16).also { it[10] = -1; it[11] = -1; bytes.copyInto(it, 12) }
    }
}
