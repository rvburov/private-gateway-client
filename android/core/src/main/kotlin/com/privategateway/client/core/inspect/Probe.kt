package com.privategateway.client.core.inspect

import java.net.IDN
import java.net.InetAddress
import java.net.UnknownHostException
import java.time.Instant

/**
 * Замер инспектора на телефоне: поиск имени, соединение TCP, рукопожатие TLS. Эхо ICMP и путь (трассировка) приложению
 * Android недоступны: сырых сокетов у него нет (решение 448).
 */
enum class ProbeKind {
    Dns,
    Tcp,
    Tls,
}

/** Чем кончилась попытка замера — как у десктопа (`ProbeOutcome`), без участка трассировки. */
enum class ProbeOutcome {
    /** Ответил: адрес имени, открытый порт, рукопожатие. */
    Ok,

    /** Ответа не было за отведенное время. */
    TimedOut,

    /** Порт закрыт: узел отказал в соединении. */
    Refused,

    /** Узел или сеть недоступны: так сказала ОС. */
    Unreachable,

    /** Имя не найдено или у него нет адресов. */
    NotFound,

    /** Другой отказ: рукопожатие TLS оборвано и подобное. */
    Failed,
}

/** Что не так с сертификатом узла. */
enum class TlsProblem {
    NameMismatch,
    Expired,
    NotYetValid,
    Untrusted,
    Other,
}

/**
 * Что показало рукопожатие TLS: версия («TLSv1.3»), набор шифров, согласованный протокол приложения (нет — null), кому и
 * кем выдан сертификат, срок, имена (DNS и IP из SAN) и что не так (все в порядке — пусто).
 */
data class TlsFacts(
    val protocol: String,
    val cipher: String,
    val alpn: String?,
    val subject: String,
    val issuer: String,
    val notBefore: Instant,
    val notAfter: Instant,
    val names: List<String>,
    val problems: List<TlsProblem>,
)

/** Строка хода замера: номер (адрес имени по порядку; у TCP и TLS — 1), кто ответил, за сколько мс, чем кончилось. */
data class ProbeStep(val index: Int, val address: String?, val timeMs: Long?, val outcome: ProbeOutcome, val tls: TlsFacts? = null)

/** Итог замера строкой: ключ текста (probe.summary.*) и подстановки — как у десктопа. */
data class ProbeSummary(val key: String, val args: List<String> = emptyList())

/** Замер целиком: что, до какого узла и порта, по шагам. */
data class ProbeResult(val kind: ProbeKind, val target: String, val port: Int?, val steps: List<ProbeStep>) {
    /** Итог — те же правила, что у десктопа (`ProbePageViewModel.Summary`). */
    fun summary(): ProbeSummary {
        val first = steps.firstOrNull() ?: return ProbeSummary("probe.summary.failed")
        if (first.outcome == ProbeOutcome.NotFound) {
            return ProbeSummary("probe.summary.notFound")
        }

        if (kind == ProbeKind.Dns) {
            return ProbeSummary("probe.summary.dns", listOf(steps.size.toString(), (first.timeMs ?: 0).toString()))
        }

        val tls = first.tls
        return when {
            first.outcome == ProbeOutcome.Ok && tls != null && tls.problems.isNotEmpty() -> ProbeSummary("probe.summary.tls.problems")
            first.outcome == ProbeOutcome.Ok && tls != null -> ProbeSummary("probe.summary.tls", listOf(tls.protocol))
            first.outcome == ProbeOutcome.Ok && kind == ProbeKind.Tls -> ProbeSummary("probe.summary.failed")
            first.outcome == ProbeOutcome.Ok -> ProbeSummary("probe.summary.tcp", listOf(port.toString(), (first.timeMs ?: 0).toString()))
            first.outcome == ProbeOutcome.Refused -> ProbeSummary("probe.summary.refused", listOf(port.toString()))
            first.outcome == ProbeOutcome.TimedOut -> ProbeSummary("probe.summary.timedOut")
            first.outcome == ProbeOutcome.Unreachable -> ProbeSummary("probe.summary.unreachable")
            else -> ProbeSummary("probe.summary.failed")
        }
    }
}

/**
 * Узел замера: IP или имя хоста — правила десктопа (`ProbeTarget`). Имя — буквы, цифры, дефис и точки (международное — в
 * виде punycode), не длиннее 253 знаков, последняя метка — не одни цифры; IPv4 — только четыре числа, IPv6 — и в скобках.
 */
object ProbeTarget {
    const val MAX_PORT = 65535

    /** Узел в виде для замера; не узел — null. Пробелы по краям и точка в конце имени отбрасываются. */
    fun normalize(text: String?): String? {
        val value = text?.trim()?.trimEnd('.')
        if (value.isNullOrEmpty()) {
            return null
        }

        ipv4(value)?.let { return it }
        // IPv6 — и в скобках, как в адресной строке; имя в скобках не пройдет проверку меток ниже.
        if (value.contains(':')) {
            return ip(value)
        }

        val ascii = try {
            if (value.all { it.code < 128 }) value.lowercase() else IDN.toASCII(value, IDN.USE_STD3_ASCII_RULES).lowercase()
        } catch (_: IllegalArgumentException) {
            return null
        }

        val labels = ascii.split('.')
        return ascii.takeIf { it.length <= 253 && labels.all(::label) && !labels.last().all(Char::isDigit) }
    }

    /** Только IP-литерал (IPv4 четырьмя числами или IPv6, и в скобках) в каноническом виде; имя и другое — null. */
    fun ip(text: String?): String? {
        val trimmed = text?.trim() ?: return null
        val value = trimmed.removeSurrounding("[", "]")
        return when {
            value.isEmpty() -> null
            value != trimmed || value.contains(':') -> if (value.contains(':')) ipv6(value) else null
            else -> ipv4(value)
        }
    }

    /** Порт замера: число от 1 до 65535; другое — null. */
    fun port(text: String?): Int? = text?.trim()?.toIntOrNull()?.takeIf { it in 1..MAX_PORT }

    private fun label(label: String) =
        label.length in 1..63 && label.first() != '-' && label.last() != '-' && label.all { it in 'a'..'z' || it in '0'..'9' || it == '-' }

    private fun ipv4(value: String): String? {
        val parts = value.split('.')
        if (parts.size != 4 || parts.any { it.isEmpty() || it.length > 3 || !it.all(Char::isDigit) }) {
            return null
        }

        val numbers = parts.map { it.toInt() }
        return if (numbers.all { it in 0..255 }) numbers.joinToString(".") else null
    }

    // IPv6 — только разбором литерала (без DNS): InetAddress принимает текст с двоеточием как адрес, а не имя.
    private fun ipv6(value: String): String? {
        if (!value.all { it.isDigit() || it in 'a'..'f' || it in 'A'..'F' || it == ':' || it == '.' }) {
            return null
        }

        return try {
            AddressText.of(InetAddress.getByName(value))
        } catch (_: UnknownHostException) {
            null
        }
    }
}
