package com.privategateway.client.vpn

import android.net.TrafficStats
import android.os.Process
import android.os.SystemClock
import javax.inject.Inject

/** Счетчики байт приложения в момент [at] (elapsedRealtime, мс): [rx] — принято, [tx] — отправлено, с начала работы ОС. */
data class ByteSample(val rx: Long, val tx: Long, val at: Long)

/** Счетчики байт приложения; Android их не дает — null. В тестах — подделка. */
fun interface ByteCounters {
    fun read(): ByteSample?
}

/**
 * Счетчики Android для своего UID (TrafficStats). Весь туннель идет через сокеты ядра в процессе приложения: пакеты других
 * программ ядро читает из карты туннеля и шлет серверу своими сокетами — их байты и есть трафик туннеля. В них же накладные
 * расходы протокола и запросы самого приложения (проверка IP, подписки) — доли процента от заметной скорости.
 */
class AppByteCounters @Inject constructor() : ByteCounters {
    override fun read(): ByteSample? {
        val uid = Process.myUid()
        val rx = TrafficStats.getUidRxBytes(uid)
        val tx = TrafficStats.getUidTxBytes(uid)
        // TrafficStats.UNSUPPORTED (−1): Android не считает трафик этого UID.
        return if (rx < 0 || tx < 0) null else ByteSample(rx, tx, SystemClock.elapsedRealtime())
    }
}

/** Скорость туннеля для карточки «Пинг · Загрузка · Отдача» (доска 01), Мбит/с — как Traffic.DownloadMbps десктопа. */
data class TrafficSpeed(val downMbps: Double, val upMbps: Double) {
    companion object {
        /** Между двумя замерами; нет одного из них, время не шло или счетчик начался заново — null: не знаем, а не «0,0». */
        fun between(before: ByteSample?, after: ByteSample?): TrafficSpeed? {
            if (before == null || after == null) {
                return null
            }

            val millis = after.at - before.at
            val rx = after.rx - before.rx
            val tx = after.tx - before.tx
            if (millis <= 0 || rx < 0 || tx < 0) {
                return null
            }

            return TrafficSpeed(mbps(rx, millis), mbps(tx, millis))
        }

        private fun mbps(bytes: Long, millis: Long) = bytes * 8.0 / millis / 1_000.0
    }
}
