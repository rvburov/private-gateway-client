package com.privategateway.client.core.countries

/**
 * Адрес, записанный цифрами: IPv4 «a.b.c.d» или IPv6 (с «::», IPv4 в конце и зоной «%…»), в квадратных скобках тоже.
 * Разбирается здесь, без InetAddress: на Android он для строки, похожей на имя, пошел бы в DNS, а страну сервера
 * приложение узнает без сети. [scoped] — у IPv6 есть зона: такой адрес локальный, страны у него нет.
 */
class IpLiteral private constructor(val bytes: ByteArray, val scoped: Boolean) {
    companion object {
        private val V4 = Regex("""(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})""")
        private val V6 = Regex("""[0-9A-Fa-f:.]+(%[0-9A-Za-z_.\-]+)?""")

        // Служебные сети, как у десктопа (CountryDatabase.Special4/Special6): страну им не назначаем.
        private val SPECIAL_V4 = listOf(
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
        ).map(::network)
        private val GLOBAL_V6 = network("2000::/3")
        private val SPECIAL_V6 = listOf("2001::/23", "2001:db8::/32", "3fff::/20").map(::network)

        fun parse(text: String): IpLiteral? {
            val value = text.trim().removeSurrounding("[", "]")
            V4.matchEntire(value)?.let { match ->
                val parts = match.groupValues.drop(1).map { it.toInt() }
                return if (parts.all { it in 0..255 }) IpLiteral(ByteArray(4) { parts[it].toByte() }, false) else null
            }

            if (!value.contains(':') || V6.matchEntire(value) == null) {
                return null
            }

            val zone = value.indexOf('%')
            val bytes = v6(if (zone >= 0) value.substring(0, zone) else value) ?: return null
            return IpLiteral(bytes, zone >= 0)
        }

        /** Публичный адрес: не частный, не служебный, не документации; IPv6 — только глобальный 2000::/3 без зоны. */
        fun isPublic(address: IpLiteral): Boolean {
            val bytes = address.bytes
            val v4 = when {
                bytes.size == 4 -> bytes
                bytes.copyOfRange(0, 10).all { it.toInt() == 0 } && bytes[10].toInt() == -1 && bytes[11].toInt() == -1 -> bytes.copyOfRange(12, 16)
                else -> null
            }
            return when {
                v4 != null -> SPECIAL_V4.none { it.contains(v4) }
                address.scoped -> false
                else -> GLOBAL_V6.contains(bytes) && SPECIAL_V6.none { it.contains(bytes) }
            }
        }

        // IPv6 без зоны: восемь групп, «::» не больше одного раза, IPv4 в конце — две группы.
        private fun v6(text: String): ByteArray? {
            val halves = text.split("::")
            if (halves.size > 2) {
                return null
            }

            val head = groups(halves[0]) ?: return null
            val tail = if (halves.size == 2) groups(halves[1]) ?: return null else emptyList()
            val missing = 8 - head.size - tail.size
            if ((halves.size == 1 && missing != 0) || (halves.size == 2 && missing < 1)) {
                return null
            }

            val all = head + List(if (halves.size == 2) missing else 0) { 0 } + tail
            return ByteArray(16) { i -> (all[i / 2] shr (if (i % 2 == 0) 8 else 0)).toByte() }
        }

        private fun groups(text: String): List<Int>? {
            if (text.isEmpty()) {
                return emptyList()
            }

            val parts = text.split(':')
            val result = mutableListOf<Int>()
            parts.forEachIndexed { index, part ->
                if (index == parts.lastIndex && part.contains('.')) {
                    val v4 = parse(part)?.bytes?.takeIf { it.size == 4 } ?: return null
                    result += ((v4[0].toInt() and 0xff) shl 8) or (v4[1].toInt() and 0xff)
                    result += ((v4[2].toInt() and 0xff) shl 8) or (v4[3].toInt() and 0xff)
                } else {
                    if (part.isEmpty() || part.length > 4 || part.any { it.digitToIntOrNull(16) == null }) {
                        return null
                    }

                    result += part.toInt(16)
                }
            }

            return result
        }

        private fun network(cidr: String): Network {
            val (address, bits) = cidr.split('/')
            return Network(parse(address)!!.bytes, bits.toInt())
        }
    }

    private class Network(val prefix: ByteArray, val bits: Int) {
        fun contains(address: ByteArray): Boolean {
            if (address.size != prefix.size) {
                return false
            }

            for (bit in 0 until bits) {
                val mask = 0x80 ushr (bit % 8)
                if ((address[bit / 8].toInt() and mask) != (prefix[bit / 8].toInt() and mask)) {
                    return false
                }
            }

            return true
        }
    }
}
