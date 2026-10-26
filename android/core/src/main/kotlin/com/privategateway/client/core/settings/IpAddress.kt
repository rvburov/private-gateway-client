package com.privategateway.client.core.settings

/**
 * Запись адреса IP — строгий разбор без поиска имен. IPv4 — ровно четыре числа 0–255 без ведущих нулей («1.2» и «010.0.0.1»
 * не адреса); IPv6 — группы по 1–4 шестнадцатеричные цифры, восемь или меньше с одним «::», последние две можно записать
 * как IPv4 («::ffff:192.0.2.1»). Зона («fe80::1%wlan0») не принимается: ядру она не нужна.
 */
object IpAddress {
    private val ipv4 = Regex("""^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$""")
    private val group = Regex("^[0-9A-Fa-f]{1,4}$")

    /** Байты адреса: 4 — IPv4, 16 — IPv6; null — не адрес. */
    fun parse(text: String): ByteArray? = ipv4(text) ?: ipv6(text)

    private fun ipv4(text: String): ByteArray? =
        if (ipv4.matches(text)) text.split('.').map { it.toInt().toByte() }.toByteArray() else null

    private fun ipv6(text: String): ByteArray? {
        // «::» — не больше одного: второй («1::2::3», «::::») оставляет пустую группу, и такой адрес не проходит.
        val gap = text.indexOf("::")
        val head = groups(if (gap >= 0) text.substring(0, gap) else text, last = gap < 0) ?: return null
        val tail = if (gap >= 0) groups(text.substring(gap + 2), last = true) ?: return null else emptyList()
        // «::» заменяет хотя бы одну группу нулей; без него групп ровно восемь.
        val count = head.size + tail.size
        val fits = if (gap >= 0) count <= 7 else count == 8
        if (!fits) {
            return null
        }

        val words = head + List(8 - count) { 0 } + tail
        return words.flatMap { listOf((it shr 8).toByte(), it.toByte()) }.toByteArray()
    }

    /** Группы части адреса по обе стороны «::»; IPv4 — только в конце адреса ([last]) и дает две группы. */
    private fun groups(part: String, last: Boolean): List<Int>? {
        if (part.isEmpty()) {
            return emptyList()
        }

        val pieces = part.split(':')
        val words = mutableListOf<Int>()
        pieces.forEachIndexed { index, piece ->
            if (last && index == pieces.lastIndex && piece.contains('.')) {
                val v4 = ipv4(piece) ?: return null
                words.add((v4[0].toInt() and 0xFF) shl 8 or (v4[1].toInt() and 0xFF))
                words.add((v4[2].toInt() and 0xFF) shl 8 or (v4[3].toInt() and 0xFF))
            } else {
                words.add(piece.takeIf { group.matches(it) }?.toInt(16) ?: return null)
            }
        }

        return words
    }
}

/**
 * Частные сети — те же, что открывает «Разрешить локальную сеть» на десктопе (`PrivateNetwork`): 10/8, 172.16/12,
 * 192.168/16, 169.254/16, fc00::/7, fe80::/10. IPv4 в IPv6 («::ffff:192.168.1.53») — как IPv4.
 */
object PrivateNetwork {
    /** Адрес из частной сети; не адрес — false. */
    fun contains(address: String): Boolean {
        val parsed = IpAddress.parse(address) ?: return false
        val bytes = if (parsed.size == 16 && mapped(parsed)) parsed.copyOfRange(12, 16) else parsed
        val first = bytes[0].toInt() and 0xFF
        val second = bytes[1].toInt() and 0xFF
        return if (bytes.size == 4) {
            first == 10 || (first == 172 && (second and 0xF0) == 16) || (first == 192 && second == 168) || (first == 169 && second == 254)
        } else {
            (first and 0xFE) == 0xFC || (first == 0xFE && (second and 0xC0) == 0x80)
        }
    }

    /** ::ffff:a.b.c.d — десять нулевых байтов и два 0xFF. */
    private fun mapped(bytes: ByteArray) = (0 until 10).all { bytes[it] == 0.toByte() } && bytes[10] == 0xFF.toByte() && bytes[11] == 0xFF.toByte()
}
