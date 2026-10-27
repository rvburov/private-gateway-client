package com.privategateway.client.core.inspect

import com.privategateway.client.core.links.JsonTree
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress

/** Адрес текстом: IPv4 — как есть, IPv6 — короткой записью RFC 5952 (как у десктопа), без зоны. */
object AddressText {
    fun of(address: InetAddress): String {
        if (address !is Inet6Address) {
            return address.hostAddress
        }

        val bytes = address.address
        val groups = IntArray(8) { ((bytes[it * 2].toInt() and 0xFF) shl 8) or (bytes[it * 2 + 1].toInt() and 0xFF) }
        // Самая длинная серия нулевых групп (от двух) сворачивается в «::»; при равных — первая.
        var bestStart = -1
        var bestLength = 1
        var index = 0
        while (index < 8) {
            if (groups[index] != 0) {
                index++
                continue
            }

            val start = index
            while (index < 8 && groups[index] == 0) {
                index++
            }

            if (index - start > bestLength) {
                bestStart = start
                bestLength = index - start
            }
        }

        if (bestStart < 0) {
            return groups.joinToString(":") { Integer.toHexString(it) }
        }

        val head = (0 until bestStart).joinToString(":") { Integer.toHexString(groups[it]) }
        val tail = (bestStart + bestLength until 8).joinToString(":") { Integer.toHexString(groups[it]) }
        return "$head::$tail"
    }
}

/** Почему сведений об адресе нет — как у десктопа (`AddressFailure`). */
enum class AddressFailure {
    /** Адрес частный или служебный: в интернете о нем ничего нет. */
    Private,

    /** Источник не ответил: нет сети, сеть закрыта, отказ сервиса. */
    Unreachable,

    /** Источник ответил, но об адресе ничего не знает. */
    NotFound,
}

/**
 * Что известно об адресе в интернете — те же сведения, что у десктопа (`AddressInfo`): автономная система и ее владелец,
 * объявленная сеть — RIPEstat, примерное место — MaxMind GeoLite2 через RIPEstat.
 */
data class AddressInfo(
    val address: String,
    val asn: Long?,
    val holder: String?,
    val prefix: String?,
    val country: String?,
    val city: String?,
    val failure: AddressFailure?,
) {
    companion object {
        fun failed(address: String, failure: AddressFailure) = AddressInfo(address, null, null, null, null, null, failure)
    }
}

/**
 * Публичный адрес и что о нем знает RIPEstat — правила `AddressLookup` десктопа: о частном, служебном и адресе
 * документации не спрашивать; в запросе — только сам адрес в каноническом виде.
 */
object PublicAddress {
    /** Открытый API RIPE NCC: без ключа и учетной записи. */
    const val SOURCE = "https://stat.ripe.net/data/"

    /** Внешний IP: Cloudflare отвечает строкой «ip=…» — тот же источник, что у десктопа. */
    const val TRACE = "https://1.1.1.1/cdn-cgi/trace"

    // Не публичные IPv4 (RFC 6890) — тот же список, что у десктопа.
    private val special4 = listOf(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
        "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
    ).map(::network)

    // IPv6 публичен только в 2000::/3 — кроме служебных IETF (и Teredo) и документации.
    private val global6 = network("2000::/3")
    private val special6 = listOf("2001::/23", "2001:db8::/32", "3fff::/20").map(::network)

    fun isPublic(address: InetAddress): Boolean = when (address) {
        is Inet4Address -> special4.none { it.contains(address) }
        is Inet6Address -> global6.contains(address) && special6.none { it.contains(address) }
        else -> false
    }

    /** Адреса запросов RIPEstat об [address]: обзор сети и место. */
    fun requests(address: InetAddress): List<String> {
        val resource = AddressText.of(address)
        return listOf(
            "${SOURCE}prefix-overview/data.json?resource=$resource&sourceapp=private-gateway",
            "${SOURCE}maxmind-geo-lite/data.json?resource=$resource&sourceapp=private-gateway",
        )
    }

    /** Внешний IP из ответа Cloudflare trace (строка «ip=…»); нет или не адрес — null. */
    fun traceIp(body: String?): String? {
        val text = body?.lineSequence()?.firstOrNull { it.startsWith("ip=") }?.removePrefix("ip=") ?: return null
        return ProbeTarget.ip(text)
    }

    /**
     * Сведения из ответов RIPEstat — как `AddressLookup.Parse` десктопа. Объявленной сети нет — ни ASN, ни владельца; страна
     * «??» или пустая — неизвестна. Нет ни одного ответа — [AddressFailure.Unreachable]; ответы есть, сведений нет —
     * [AddressFailure.NotFound].
     */
    fun parse(address: String, overview: String?, geo: String?): AddressInfo {
        val (asn, holder, prefix) = overview(overview)
        val (country, city) = location(geo)
        return when {
            overview == null && geo == null -> AddressInfo.failed(address, AddressFailure.Unreachable)
            asn == null && country == null -> AddressInfo.failed(address, AddressFailure.NotFound)
            else -> AddressInfo(address, asn, holder, prefix, country, city, null)
        }
    }

    private fun overview(json: String?): Triple<Long?, String?, String?> {
        val data = data(json) ?: return Triple(null, null, null)
        val first = (data["asns"] as? List<*>)?.firstOrNull() as? Map<*, *>
        val asn = (first?.get("asn") as? JsonTree.JsonNumber)?.text?.toLongOrNull()?.takeIf { it > 0 }
        val holder = if (asn != null) text(first, "holder") else null
        // Сеть — только объявленная: без объявления RIPEstat возвращает сам адрес.
        val prefix = text(data, "resource")?.takeIf { data["announced"] == true && it.contains('/') }
        return Triple(asn, holder, prefix)
    }

    private fun location(json: String?): Pair<String?, String?> {
        val located = data(json)?.get("located_resources") as? List<*> ?: return null to null
        for (resource in located) {
            val locations = (resource as? Map<*, *>)?.get("locations") as? List<*> ?: continue
            for (location in locations.filterIsInstance<Map<*, *>>()) {
                val country = text(location, "country")
                if (country != null && country.length == 2 && country.all { it in 'a'..'z' || it in 'A'..'Z' }) {
                    return country.uppercase() to text(location, "city")
                }
            }
        }

        return null to null
    }

    // «data» ответа со статусом «ok»; другое или не JSON — null.
    private fun data(json: String?): Map<*, *>? {
        val root = try {
            JsonTree.parse(json ?: return null).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        } ?: return null

        return if (text(root, "status") == "ok") root["data"] as? Map<*, *> else null
    }

    private fun text(node: Map<*, *>?, key: String): String? = (node?.get(key) as? String)?.trim()?.takeIf { it.isNotEmpty() }

    private class Network(val base: ByteArray, val bits: Int) {
        fun contains(address: InetAddress): Boolean {
            val bytes = address.address
            if (bytes.size != base.size) {
                return false
            }

            for (bit in 0 until bits) {
                val mask = 0x80 ushr (bit % 8)
                if ((bytes[bit / 8].toInt() and mask) != (base[bit / 8].toInt() and mask)) {
                    return false
                }
            }

            return true
        }
    }

    private fun network(cidr: String): Network {
        val (address, bits) = cidr.split('/')
        return Network(InetAddress.getByName(address).address, bits.toInt())
    }
}
