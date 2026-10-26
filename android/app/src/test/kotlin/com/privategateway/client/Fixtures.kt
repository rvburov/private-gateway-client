package com.privategateway.client

import com.privategateway.client.core.links.LinkParser
import com.privategateway.client.core.links.LinkWriter
import com.privategateway.client.core.servers.ServerDefinition
import com.privategateway.client.data.SecretBox
import java.io.File

/** Общие эталоны (shared/testdata) и заглушки для тестов приложения. */
object Fixtures {
    val testdata = File(requireNotNull(System.getProperty("testdata")) { "Нет пути к shared/testdata" })

    val localization: File = testdata.resolveSibling("specifications").resolve("localization")

    /** Все серверы из эталонных ссылок, которые разбираются. */
    fun servers(): List<ServerDefinition> = testdata.resolve("links").walk()
        .filter { it.isFile && it.name.endsWith(".txt") }
        .sortedBy { it.path }
        .mapNotNull { LinkParser.parse(it.readText().trim()).server }
        .toList()

    /** Тот же сервер под другим названием — тот же «отпечаток». */
    fun identity(server: ServerDefinition) = LinkWriter.write(server.copy(name = "", countryCode = ""))
}

/**
 * «Шифрование» для тестов: каждый байт — XOR с постоянной, впереди метка; видно, что данные прошли через ящик. Обращение
 * байтов не годится: ключ из одинаковых знаков («AAAA…=») читался бы и обращенным. Keystore в Robolectric нет.
 */
class FakeBox(private val delayMillis: Long = 0) : SecretBox {
    @Volatile
    var sealed = 0

    override fun seal(plain: ByteArray): ByteArray {
        // Медленный ящик (как Keystore) — чтобы два добавления сразу точно шли одновременно.
        Thread.sleep(delayMillis)
        sealed++
        return MARK + flip(plain)
    }

    override fun open(sealed: ByteArray): ByteArray {
        require(sealed.size >= MARK.size && sealed.copyOfRange(0, MARK.size).contentEquals(MARK)) { "чужие данные" }
        return flip(sealed.copyOfRange(MARK.size, sealed.size))
    }

    private fun flip(bytes: ByteArray) = ByteArray(bytes.size) { (bytes[it].toInt() xor 0x5A).toByte() }

    private companion object {
        val MARK = byteArrayOf(0x50, 0x47, 0x01)
    }
}
