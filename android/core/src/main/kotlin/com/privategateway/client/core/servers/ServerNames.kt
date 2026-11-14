package com.privategateway.client.core.servers

/** Свое название сервера — правило карточки десктопа (`ServerCardViewModel.CanSaveName`, `Rename` каталога). */
object ServerNames {
    /** Длина своего названия, без пробелов по краям. */
    const val MAX_LENGTH = 64

    /** Название к сохранению: без пробелов по краям; пустое или длиннее [MAX_LENGTH] — null, кнопка «Сохранить» гаснет. */
    fun validate(name: String): String? = name.trim().takeIf { it.length in 1..MAX_LENGTH }

    /** Только показ: отделяем эмодзи от слов и соседних эмодзи, исходное название и экспорт не меняются. */
    fun forDisplay(name: String): String = buildString(name.length) {
        var index = 0
        var previous = Kind.Other
        while (index < name.length) {
            val start = index
            val first = name.codePointAt(index)
            index += Character.charCount(first)
            // Не разделяем пару региональных индикаторов флага.
            if (first in 0x1F1E6..0x1F1FF && index < name.length && name.codePointAt(index) in 0x1F1E6..0x1F1FF) {
                index += Character.charCount(name.codePointAt(index))
            }
            // Android и JVM могут иметь разные версии сегментации Unicode: явно сохраняем ZWJ, модификаторы и метки.
            while (index < name.length) {
                val next = name.codePointAt(index)
                if (isContinuation(next)) {
                    index += Character.charCount(next)
                } else if (next == 0x200D) {
                    index++
                    if (index < name.length && isEmoji(first) && isEmoji(name.codePointAt(index))) {
                        index += Character.charCount(name.codePointAt(index))
                    } else {
                        break
                    }
                } else {
                    break
                }
            }
            val element = name.substring(start, index)
            val current = when {
                isEmoji(first) || '\uFE0F' in element || '\u20E3' in element -> Kind.Emoji
                Character.isLetterOrDigit(first) -> Kind.Word
                else -> Kind.Other
            }
            if ((previous == Kind.Emoji && (current == Kind.Emoji || current == Kind.Word)) || (previous == Kind.Word && current == Kind.Emoji)) append(' ')
            append(element)
            previous = current
        }
    }

    private fun isContinuation(code: Int): Boolean =
        Character.getType(code) in MARK_TYPES ||
            code in 0x1F3FB..0x1F3FF || code in 0xE0020..0xE007F

    private fun isEmoji(code: Int): Boolean = code in 0x1F000..0x1FAFF || code in 0x2600..0x27BF ||
        code in 0x231A..0x231B || code in 0x23E9..0x23EC || code == 0x23F0 || code == 0x23F3 ||
        code in 0x25FD..0x25FE || code == 0x2B50 || code == 0x2B55

    private val MARK_TYPES = setOf(Character.NON_SPACING_MARK.toInt(), Character.COMBINING_SPACING_MARK.toInt(), Character.ENCLOSING_MARK.toInt())

    private enum class Kind { Other, Word, Emoji }
}
