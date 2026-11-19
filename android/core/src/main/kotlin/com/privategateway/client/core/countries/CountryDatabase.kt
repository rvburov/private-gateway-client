package com.privategateway.client.core.countries

import java.io.EOFException
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.channels.FileChannel

/**
 * Локальная оценка страны IP — формат PGCTRY1, общий с десктопом (shared/countries/country.bin.gz, DB-IP Country Lite):
 * заголовок «PGCTRY1\0» и число записей (4 байта, big-endian), затем записи по 34 байта — начало и конец диапазона
 * (16 + 16 байт, IPv4 — как ::ffff:a.b.c.d) и код ISO 3166-1 из двух латинских букв. Диапазоны идут по возрастанию и не
 * пересекаются — иначе база не читается. Нет записи — нет флага. В сеть не ходит: DNS и названия серверов не используются.
 */
class CountryDatabase private constructor(private val data: ByteBuffer, val count: Int) {
    /** Код страны адреса (16 байт IPv6 или 4 байта IPv4) или null. Без фильтра служебных адресов — для тестовых баз. */
    private fun find(address: ByteArray): String? {
        val key = mapped(address) ?: return null
        var low = 0
        var high = count - 1
        while (low <= high) {
            val middle = (low + high) ushr 1
            val at = HEADER_SIZE + middle * RECORD_SIZE
            when {
                compare(key, at) < 0 -> high = middle - 1
                compare(key, at + 16) > 0 -> low = middle + 1
                else -> return String(charArrayOf(data.get(at + 32).toInt().toChar(), data.get(at + 33).toInt().toChar()))
            }
        }

        return null
    }

    /** Код страны адреса без фильтра служебных сетей (тестовые базы на адресах документации); адрес с зоной — null. */
    fun find(address: IpLiteral): String? = if (address.scoped) null else find(address.bytes)

    /** Страна адреса сервера: только адрес цифрами и только публичный; частные, служебные и адреса документации — null. */
    fun lookup(host: String): String? {
        val address = IpLiteral.parse(host) ?: return null
        return if (IpLiteral.isPublic(address)) find(address) else null
    }

    // Сравнение адреса с началом или концом записи как беззнаковых 16 байт.
    private fun compare(key: ByteArray, at: Int): Int {
        for (i in 0 until 16) {
            val difference = (key[i].toInt() and 0xff) - (data.get(at + i).toInt() and 0xff)
            if (difference != 0) {
                return difference
            }
        }

        return 0
    }

    companion object {
        private const val HEADER_SIZE = 12
        private const val RECORD_SIZE = 34
        private const val MAX_RECORDS = 2_000_000
        private val MAGIC = "PGCTRY1\u0000".toByteArray(Charsets.US_ASCII)

        /** Вся база из потока (уже распакованного) — в память; лишний байт после записей — ошибка. */
        fun read(input: InputStream): CountryDatabase {
            val header = input.readNBytesExactly(HEADER_SIZE)
            val count = count(header)
            val bytes = ByteArray(HEADER_SIZE + count * RECORD_SIZE)
            header.copyInto(bytes)
            readFully(input, bytes, HEADER_SIZE)
            if (input.read() != -1) {
                throw IOException("Лишние данные геобазы")
            }

            return checked(ByteBuffer.wrap(bytes), count)
        }

        /** База из распакованного файла — отображением в память: 25 МБ записей не занимают кучу приложения. */
        fun open(file: File): CountryDatabase = RandomAccessFile(file, "r").use { raf ->
            val channel = raf.channel
            val header = ByteArray(HEADER_SIZE)
            if (channel.size() < HEADER_SIZE) {
                throw EOFException("Геобаза обрезана")
            }

            raf.readFully(header)
            val count = count(header)
            val expected = HEADER_SIZE.toLong() + count.toLong() * RECORD_SIZE
            if (channel.size() < expected) {
                throw EOFException("Геобаза обрезана")
            }

            if (channel.size() > expected) {
                throw IOException("Лишние данные геобазы")
            }

            checked(channel.map(FileChannel.MapMode.READ_ONLY, 0, expected), count)
        }

        private fun count(header: ByteArray): Int {
            if (!header.copyOfRange(0, 8).contentEquals(MAGIC)) {
                throw IOException("Неизвестный формат геобазы")
            }

            val count = ByteBuffer.wrap(header, 8, 4).int.toLong() and 0xffffffffL
            if (count > MAX_RECORDS) {
                throw IOException("Геобаза слишком велика")
            }

            return count.toInt()
        }

        // Каждый диапазон: начало ≤ конца, код — две буквы A–Z, начало строго после конца предыдущего.
        private fun checked(data: ByteBuffer, count: Int): CountryDatabase {
            val database = CountryDatabase(data, count)
            val previousEnd = ByteArray(16)
            for (i in 0 until count) {
                val at = HEADER_SIZE + i * RECORD_SIZE
                val start = ByteArray(16) { data.get(at + it) }
                val end = ByteArray(16) { data.get(at + 16 + it) }
                val letters = (data.get(at + 32).toInt() and 0xff) in 'A'.code..'Z'.code && (data.get(at + 33).toInt() and 0xff) in 'A'.code..'Z'.code
                if (unsigned(start, end) > 0 || !letters || (i > 0 && unsigned(previousEnd, start) >= 0)) {
                    throw IOException("Некорректный диапазон геобазы")
                }

                end.copyInto(previousEnd)
            }

            return database
        }

        private fun unsigned(a: ByteArray, b: ByteArray): Int {
            for (i in a.indices) {
                val difference = (a[i].toInt() and 0xff) - (b[i].toInt() and 0xff)
                if (difference != 0) {
                    return difference
                }
            }

            return 0
        }

        // IPv4 — как ::ffff:a.b.c.d, IPv6 — как есть; иначе null.
        private fun mapped(address: ByteArray): ByteArray? = when (address.size) {
            16 -> address
            4 -> ByteArray(16).also { it[10] = -1; it[11] = -1; address.copyInto(it, 12) }
            else -> null
        }

        private fun InputStream.readNBytesExactly(size: Int): ByteArray = ByteArray(size).also { readFully(this, it, 0) }

        private fun readFully(input: InputStream, into: ByteArray, from: Int) {
            var offset = from
            while (offset < into.size) {
                val read = input.read(into, offset, into.size - offset)
                if (read < 0) {
                    throw EOFException("Геобаза обрезана")
                }

                offset += read
            }
        }
    }
}
