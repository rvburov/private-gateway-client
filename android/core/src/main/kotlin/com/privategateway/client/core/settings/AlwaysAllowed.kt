package com.privategateway.client.core.settings

/**
 * «Пропускается всегда» при «Остальные сайты: Блокировать» (решение 740): обновления системы и защиты, точное время, проверка
 * сети, уведомления и проверка сертификатов — без них телефон ломается. Домен — с поддоменами. Тот же список —
 * shared/specifications/routing/always-allowed.json, тест сверяет. Сайт Private Gateway добавляет конфиг ядра сам.
 */
object AlwaysAllowed {
    val updates = listOf(
        "windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com", "dsp.mp.microsoft.com", "wdcp.microsoft.com",
        "wdcpalt.microsoft.com", "definitionupdates.microsoft.com", "swscan.apple.com", "swcdn.apple.com", "swdist.apple.com",
        "mesu.apple.com", "updates.cdn-apple.com", "play.googleapis.com", "android.clients.google.com",
    )

    val time = listOf("time.windows.com", "time.apple.com", "time.google.com", "time.android.com", "pool.ntp.org")

    val network = listOf(
        "msftconnecttest.com", "msftncsi.com", "captive.apple.com", "connectivitycheck.gstatic.com", "connectivitycheck.android.com",
        "clients3.google.com", "nmcheck.gnome.org",
    )

    val notifications = listOf(
        "mtalk.google.com", "alt1-mtalk.google.com", "alt2-mtalk.google.com", "alt3-mtalk.google.com", "alt4-mtalk.google.com",
        "alt5-mtalk.google.com", "alt6-mtalk.google.com", "alt7-mtalk.google.com", "alt8-mtalk.google.com", "fcm.googleapis.com",
        "push.apple.com",
    )

    val certificates = listOf("digicert.com", "pki.goog", "lencr.org", "sectigo.com", "usertrust.com", "globalsign.com", "entrust.net")

    /** Все домены списка по порядку групп. */
    val domains: List<String> = updates + time + network + notifications + certificates

    /** UDP-порты, которые пропускаются по номеру: время (NTP) имени в пакете не несет. */
    val udpPorts = listOf(123)
}
