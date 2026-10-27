package com.privategateway.client.core.inspect

import java.io.IOException
import java.net.ConnectException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NoRouteToHostException
import java.net.PortUnreachableException
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.CertificateExpiredException
import java.security.cert.CertificateNotYetValidException
import java.security.cert.X509Certificate
import java.time.Instant
import java.util.Locale
import javax.net.ssl.SNIHostName
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/**
 * Замеры инспектора из процесса приложения — как `ProbeRunner` десктопа, только поиск имени, TCP и TLS. Под защитой
 * приложение идет через свой туннель (оно из него не исключено) — замер видит сеть так же, как программы телефона.
 * Ничего не пишет в журнал: узел — то, что ввел пользователь. Блокирует поток — вызывать не из главного.
 */
class ProbeRunner(
    private val resolve: (String) -> List<InetAddress> = { InetAddress.getAllByName(it).toList() },
    private val trust: X509TrustManager = defaultTrust(),
    private val now: () -> Instant = Instant::now,
    private val timeoutMs: Int = TIMEOUT_MS,
) {
    /** Поиск имени через DNS системы — те же адреса получают программы. */
    fun dns(target: String): ProbeResult {
        val started = System.nanoTime()
        val addresses = try {
            resolve(target)
        } catch (_: UnknownHostException) {
            return ProbeResult(ProbeKind.Dns, target, null, listOf(ProbeStep(1, null, null, ProbeOutcome.NotFound)))
        }

        val ms = elapsed(started)
        val steps = addresses.mapIndexed { index, address -> ProbeStep(index + 1, AddressText.of(address), ms, ProbeOutcome.Ok) }
        return ProbeResult(ProbeKind.Dns, target, null, steps.ifEmpty { listOf(ProbeStep(1, null, null, ProbeOutcome.NotFound)) })
    }

    /** Соединение TCP без данных: порт открыт, если узел ответил на рукопожатие. */
    fun tcp(target: String, port: Int): ProbeResult = connect(ProbeKind.Tcp, target, port) { socket, address, started ->
        socket.close()
        ProbeStep(1, address, elapsed(started), ProbeOutcome.Ok)
    }

    /**
     * Рукопожатие TLS без отправки данных: версия, шифр, ALPN и сертификат. Сертификат проверяется отдельно от рукопожатия
     * (доверенные корни телефона, срок, имя) — так видно, что именно с ним не так. Отзыв сертификата не проверяется.
     */
    fun tls(target: String, port: Int): ProbeResult = connect(ProbeKind.Tls, target, port) { socket, address, started ->
        val chain = mutableListOf<X509Certificate>()
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(Capture(chain)), null)
        val tls = context.socketFactory.createSocket(socket, target, port, true) as SSLSocket
        tls.use {
            tls.soTimeout = timeoutMs
            val parameters = tls.sslParameters
            if (ProbeTarget.ip(target) == null) {
                parameters.serverNames = listOf(SNIHostName(target))
            }

            alpn { parameters.applicationProtocols = arrayOf("h2", "http/1.1") }
            tls.sslParameters = parameters
            try {
                tls.startHandshake()
            } catch (_: SocketTimeoutException) {
                return@connect ProbeStep(1, address, null, ProbeOutcome.TimedOut)
            } catch (_: IOException) {
                return@connect ProbeStep(1, address, null, ProbeOutcome.Failed)
            }

            val leaf = chain.firstOrNull() ?: return@connect ProbeStep(1, address, null, ProbeOutcome.Failed)
            val session = tls.session
            val facts = TlsFacts(
                protocol = session.protocol,
                cipher = session.cipherSuite,
                alpn = alpn { tls.applicationProtocol }?.takeIf { it.isNotEmpty() },
                subject = leaf.subjectX500Principal.name,
                issuer = leaf.issuerX500Principal.name,
                notBefore = leaf.notBefore.toInstant(),
                notAfter = leaf.notAfter.toInstant(),
                names = names(leaf),
                problems = problems(chain, target),
            )
            ProbeStep(1, address, elapsed(started), ProbeOutcome.Ok, facts)
        }
    }

    /** Что не так с сертификатом: срок по часам телефона, имя по SAN, цепочка — доверенными корнями телефона. */
    internal fun problems(chain: List<X509Certificate>, target: String): List<TlsProblem> {
        val leaf = chain.first()
        val problems = mutableListOf<TlsProblem>()
        val time = now()
        when {
            time.isAfter(leaf.notAfter.toInstant()) -> problems += TlsProblem.Expired
            time.isBefore(leaf.notBefore.toInstant()) -> problems += TlsProblem.NotYetValid
        }

        if (!Names.matches(names(leaf), target)) {
            problems += TlsProblem.NameMismatch
        }

        try {
            // «UNKNOWN» — как у TLS 1.3: тип обмена ключами для проверки цепочки не важен.
            trust.checkServerTrusted(chain.toTypedArray(), "UNKNOWN")
        } catch (e: CertificateException) {
            // Цепочка не прошла из-за срока — это уже сказано; иначе — корня нет или другая ошибка цепочки.
            val dated = generateSequence<Throwable>(e) { it.cause }.any { it is CertificateExpiredException || it is CertificateNotYetValidException }
            if (!dated || problems.none { it == TlsProblem.Expired || it == TlsProblem.NotYetValid }) {
                problems += if (dated) TlsProblem.Other else TlsProblem.Untrusted
            }
        }

        return problems
    }

    private fun connect(kind: ProbeKind, target: String, port: Int, handshake: (Socket, String, Long) -> ProbeStep): ProbeResult {
        val address = try {
            resolve(target).firstOrNull()
        } catch (_: UnknownHostException) {
            null
        } ?: return ProbeResult(kind, target, port, listOf(ProbeStep(1, null, null, ProbeOutcome.NotFound)))

        val text = AddressText.of(address)
        val socket = Socket()
        val started = System.nanoTime()
        val step = try {
            socket.connect(InetSocketAddress(address, port), timeoutMs)
            handshake(socket, text, started)
        } catch (_: SocketTimeoutException) {
            ProbeStep(1, text, null, ProbeOutcome.TimedOut)
        } catch (_: ConnectException) {
            ProbeStep(1, text, null, ProbeOutcome.Refused)
        } catch (_: NoRouteToHostException) {
            ProbeStep(1, text, null, ProbeOutcome.Unreachable)
        } catch (_: PortUnreachableException) {
            ProbeStep(1, text, null, ProbeOutcome.Unreachable)
        } catch (_: IOException) {
            ProbeStep(1, text, null, ProbeOutcome.Failed)
        } finally {
            socket.close()
        }

        return ProbeResult(kind, target, port, listOf(step))
    }

    /** Сертификат принимается на время рукопожатия и запоминается: проверяется он потом, по пунктам. */
    private class Capture(private val chain: MutableList<X509Certificate>) : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("клиентом не бываем")

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            this.chain.clear()
            chain?.let { this.chain.addAll(it) }
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    /** Имена сертификата для проверки: DNS и IP из SAN; SAN нет — имя из CN, как делают старые проверки. */
    object Names {
        fun matches(names: List<String>, target: String): Boolean {
            val ip = ProbeTarget.ip(target)
            val host = target.lowercase(Locale.ROOT)
            return names.any { name -> if (ip != null) ProbeTarget.ip(name) == ip else wildcard(name.lowercase(Locale.ROOT), host) }
        }

        // «*.example.com» подходит ровно одной метке слева и не подходит самому example.com.
        private fun wildcard(pattern: String, host: String): Boolean {
            if (!pattern.startsWith("*.")) {
                return pattern == host
            }

            val suffix = pattern.substring(1)
            return host.endsWith(suffix) && host.length > suffix.length && !host.substring(0, host.length - suffix.length).contains('.')
        }
    }

    companion object {
        /** Сколько ждать соединения и рукопожатия — как у десктопа. */
        const val TIMEOUT_MS = 5_000

        private const val DNS_NAME = 2
        private const val IP_ADDRESS = 7

        /** Доверенные корни телефона (или JDK в тестах) — тот же выбор, что у любого приложения. */
        fun defaultTrust(): X509TrustManager {
            val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
            factory.init(null as KeyStore?)
            return factory.trustManagers.filterIsInstance<X509TrustManager>().first()
        }

        internal fun names(certificate: X509Certificate): List<String> {
            val san = certificate.subjectAlternativeNames
                ?.mapNotNull { entry -> (entry[1] as? String)?.takeIf { entry[0] == DNS_NAME || entry[0] == IP_ADDRESS } }
                .orEmpty()
            if (san.isNotEmpty()) {
                return san
            }

            return certificate.subjectX500Principal.name.split(',').map { it.trim() }.filter { it.startsWith("CN=") }.map { it.removePrefix("CN=") }
        }

        private fun elapsed(started: Long) = (System.nanoTime() - started) / 1_000_000

        // ALPN — с Android 10 (API 29); на Android 9 замер идет без него.
        private inline fun <T> alpn(block: () -> T): T? = try {
            block()
        } catch (_: NoSuchMethodError) {
            null
        } catch (_: UnsupportedOperationException) {
            null
        }
    }
}
