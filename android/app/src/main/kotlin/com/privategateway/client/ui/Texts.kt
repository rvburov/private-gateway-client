package com.privategateway.client.ui

import android.os.LocaleList
import androidx.annotation.StringRes
import com.privategateway.client.R
import com.privategateway.client.core.links.ImportErrors
import com.privategateway.client.core.links.ImportParser
import com.privategateway.client.core.text.Plural
import com.privategateway.client.vpn.TunnelErrors

/**
 * Тексты по кодам: ключи общие для всех приложений (shared/specifications/localization), здесь — явные соответствия
 * вместо поиска по имени: опечатка в ключе — ошибка компиляции, а не пустая строка на экране.
 */
object Texts {
    /** error.<код> — для кодов ошибок туннеля. */
    @StringRes
    fun error(code: String?): Int = when (code) {
        TunnelErrors.CORE_FAILED -> R.string.error_PGC_SVC_002
        TunnelErrors.NO_SERVER -> R.string.error_PGC_SVC_003
        TunnelErrors.UNSUPPORTED -> R.string.error_PGC_SVC_005
        TunnelErrors.OTHER_VPN -> R.string.error_PGC_NET_010
        else -> R.string.error_PGC_SVC_002
    }

    /** import.error.<код> — почему строка импорта пропущена. */
    @StringRes
    fun importError(code: String?): Int = when (code) {
        ImportErrors.UNKNOWN_SCHEME -> R.string.import_error_unknown_scheme
        ImportErrors.BAD_HOST -> R.string.import_error_bad_host
        ImportErrors.BAD_PORT -> R.string.import_error_bad_port
        ImportErrors.NO_CREDENTIALS -> R.string.import_error_no_credentials
        ImportErrors.BAD_ENCODING -> R.string.import_error_bad_encoding
        ImportErrors.UNSUPPORTED_TRANSPORT -> R.string.import_error_unsupported_transport
        ImportErrors.UNSUPPORTED_METHOD -> R.string.import_error_unsupported_method
        ImportErrors.UNSUPPORTED_PARAMETER -> R.string.import_error_unsupported_parameter
        else -> R.string.import_error_bad_link
    }

    /** import.format.<формат>. */
    @StringRes
    fun importFormat(format: String): Int = when (format) {
        ImportParser.FORMAT_BASE64 -> R.string.import_format_base64
        ImportParser.FORMAT_SINGBOX -> R.string.import_format_singbox
        ImportParser.FORMAT_CLASH -> R.string.import_format_clash
        else -> R.string.import_format_links
    }

    /**
     * Язык текстов на экране — для форм чисел. Не первый язык телефона: при «украинский, русский» Android берет русские
     * ресурсы (украинских нет), и формы должны быть русскими.
     */
    fun language(locales: LocaleList): String = locales.getFirstMatch(arrayOf("en", "ru"))?.language ?: "en"

    /** «Добавить 3 сервера». */
    @StringRes
    fun importAdd(count: Int, language: String): Int = when (Plural.form(count, language)) {
        "one" -> R.string.import_add_one
        "few" -> R.string.import_add_few
        else -> R.string.import_add_many
    }

    /** «Добавлено 3 сервера». */
    @StringRes
    fun imported(count: Int, language: String): Int = when (Plural.form(count, language)) {
        "one" -> R.string.import_added_one
        "few" -> R.string.import_added_few
        else -> R.string.import_added_many
    }

    /** «2 ошибки». */
    @StringRes
    fun importErrors(count: Int, language: String): Int = when (Plural.form(count, language)) {
        "one" -> R.string.import_errors_one
        "few" -> R.string.import_errors_few
        else -> R.string.import_errors_many
    }
}
