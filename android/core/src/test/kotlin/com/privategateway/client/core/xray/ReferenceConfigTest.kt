package com.privategateway.client.core.xray

import com.privategateway.client.core.links.LinkParser
import org.junit.jupiter.api.Assumptions.assumeTrue
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * Регрессия этапа 11: конфиг каждой эталонной ссылки shared/testdata/links читает само ядро телефона — Xray-core той
 * версии, что внутри libXray (`scripts/get-xray-mobile.sh`): `xray run -test` собирает обработчики, как `TestXray`
 * libXray, без запуска. Туннель, замер задержки, настройки по умолчанию и все включенное. Тесты конфига сверяют поля, а
 * это — что ядро закрепленной версии принимает их все: поле или значение, которых оно не знает, падает здесь, а не у
 * пользователя на «Подключении». Тот же тест у десктопа — `ReferenceConfigTests`, у iOS — `ReferenceConfigTests`.
 */
class ReferenceConfigTest {
    private val testData = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata"))

    /** Все, что по умолчанию выключено и меняет конфиг: свой DNS в сети, IPv6, локальная сеть, Mux, отпечаток, журнал. */
    private val everything = CoreSettings(
        dns = "https://dns.quad9.net/dns-query",
        allowLan = true,
        ipv6 = true,
        mux = true,
        fingerprint = "random",
        sniffing = false,
        mtu = 1400,
        debugLog = true,
        lanDns = "192.168.1.53",
    )

    @TestFactory
    fun `xray of libXray accepts the config of each reference link`() = fixtures().map { (fixture, link) ->
        DynamicTest.dynamicTest(fixture) {
            val xray = xray()
            val server = LinkParser.parse(link).server ?: return@dynamicTest
            val tunnel = XrayConfig.build(server, CoreSettings(), tunFd = 42)
            // Протокол или транспорт, которых у ядра телефона нет, — отказ до ядра, и он должен быть единственной причиной.
            tunnel.rejection?.let { rejection ->
                assertTrue(rejection != CoreRejection.MissingCredentials, "$fixture: эталон с данными доступа отвергнут: $rejection")
                assertEquals(rejection, PingBatch.outbounds(server, CoreSettings()).rejection, fixture)
                return@dynamicTest
            }

            accepted(xray, tunnel.json!!, "$fixture, туннель")
            accepted(xray, XrayConfig.build(server, everything, tunFd = 42).json!!, "$fixture, туннель, все включено")
            accepted(xray, PingBatch.outbounds(server, CoreSettings()).json!!, "$fixture, замер задержки")
            accepted(xray, PingBatch.outbounds(server, everything).json!!, "$fixture, замер задержки, все включено")
        }
    }

    /** Эталонные ссылки — файлы .txt в shared/testdata/links, текст целиком, как у `LinkParserTest`. */
    private fun fixtures(): List<Pair<String, String>> =
        testData.resolve("links").walkTopDown().filter { it.isFile && it.name.endsWith(".txt") }
            .map { it.relativeTo(testData).invariantSeparatorsPath.removeSuffix(".txt") to it.readText() }
            .sortedBy { it.first }.toList()

    /**
     * Xray libXray: путь — свойство `xrayMobile` (его задает сборка, artifacts/cores/xray-mobile). Нет файла — тест
     * пропускается с причиной, а в CI (`PG_REQUIRE_XRAY_MOBILE=1`) падает: проверка не может тихо перестать идти.
     */
    private fun xray(): File {
        val file = File(System.getProperty("xrayMobile") ?: error("Нет пути к Xray libXray"))
        if (System.getenv("PG_REQUIRE_XRAY_MOBILE") == "1" && !file.isFile) {
            fail("Нет Xray libXray: ${file.path} — scripts/get-xray-mobile.sh")
        }

        assumeTrue(file.isFile, "нет Xray libXray — scripts/get-xray-mobile.sh")
        return file
    }

    private fun accepted(xray: File, config: String, what: String) {
        val file = File.createTempFile("xray-", ".json")
        try {
            file.writeText(config)
            val process = ProcessBuilder(xray.path, "run", "-test", "-c", file.path).redirectErrorStream(true).start()
            val output = process.inputStream.bufferedReader().readText()
            assertTrue(process.waitFor(60, TimeUnit.SECONDS), "$what: xray не ответил за минуту")
            assertEquals(0, process.exitValue(), "$what: ядро не приняло конфиг\n$output\n$config")
        } finally {
            file.delete()
        }
    }
}
