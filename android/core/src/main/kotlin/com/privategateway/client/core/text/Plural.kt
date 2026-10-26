package com.privategateway.client.core.text

/**
 * Форма числа для ключей с окончаниями .one, .few и .many («import.add.few» — «Добавить 3 сервера»), как `Loc.Plural`
 * десктопа: русский — по последним цифрам (1, 21 — one; 2–4, 22–24 — few; 11–14 и остальные — many), английский — one
 * только для 1.
 */
object Plural {
    fun form(count: Int, language: String): String {
        if (language != "ru") {
            return if (count == 1) "one" else "many"
        }

        val tens = count % 100
        return when {
            count % 10 == 1 && tens != 11 -> "one"
            count % 10 in 2..4 && (tens < 12 || tens > 14) -> "few"
            else -> "many"
        }
    }
}
