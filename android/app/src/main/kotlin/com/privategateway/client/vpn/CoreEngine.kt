package com.privategateway.client.vpn

import com.privategateway.client.core.xray.XrayConfig
import com.privategateway.client.core.xray.XrayResponse
import libXray.DialerController
import libXray.LibXray
import org.json.JSONException
import org.json.JSONObject
import javax.inject.Inject

/** Ядро в процессе приложения: запуск с готовым конфигом, остановка, версия. */
interface CoreEngine {
    /**
     * Запустить ядро. [protect] выводит сокет ядра из туннеля (VpnService.protect), [dns] — сервер DNS для адреса самого
     * сервера («ip:53»). Запустилось — null, иначе текст ошибки ядра (в журнал, не пользователю).
     */
    fun start(configJson: String, protect: (Int) -> Boolean, dns: String): String?

    /** Сеть телефона сменилась: адрес сервера ядро ищет через DNS новой сети. Удалось — null, иначе текст ошибки. */
    fun changeDns(dns: String): String?

    fun stop()

    fun version(): String?
}

/**
 * Xray-core через libXray (XTLS, MIT; gomobile): Invoke с JSON, apiVersion 3. Сокеты ядра защищаются от туннеля
 * контроллером — иначе соединение с сервером ушло бы в сам туннель. Один рантайм Go на процесс: других библиотек Go в
 * приложении нет.
 */
class LibXrayEngine @Inject constructor() : CoreEngine {
    override fun start(configJson: String, protect: (Int) -> Boolean, dns: String): String? {
        Dialer.attach(protect)
        changeDns(dns)?.let { return it }
        val response = XrayResponse.parse(LibXray.invoke(XrayConfig.runRequest(configJson)))
        return if (response.success) null else response.error ?: "runXray"
    }

    override fun changeDns(dns: String): String? = try {
        LibXray.setDNS(Dialer, dns)
        null
    } catch (e: Exception) {
        "setDNS: ${e.message}"
    }

    override fun stop() {
        LibXray.invoke(XrayConfig.invoke("stopXray"))
        LibXray.resetDNS()
        Dialer.detach()
    }

    override fun version(): String? = try {
        JSONObject(LibXray.invoke(XrayConfig.invoke("xrayVersion"))).optJSONObject("data")?.optString("version")?.takeIf { it.isNotEmpty() }
    } catch (_: JSONException) {
        null
    }

    /**
     * Контроллер сокетов — один на процесс: Xray хранит каждый зарегистрированный до конца процесса и снять его не дает.
     * Новый на каждое подключение копил бы контроллеры (и службы за ними), а каждое соединение ядра звало бы их все.
     * Меняется только служба за ним; без службы сокет не защищается — соединение не идет, мимо туннеля ничего не уходит.
     */
    private object Dialer : DialerController {
        @Volatile
        private var protect: ((Int) -> Boolean)? = null
        private var registered = false

        override fun protectFd(fd: Long): Boolean = protect?.invoke(fd.toInt()) ?: false

        @Synchronized
        fun attach(protect: (Int) -> Boolean) {
            this.protect = protect
            if (!registered) {
                LibXray.registerDialerController(this)
                registered = true
            }
        }

        fun detach() {
            protect = null
        }
    }
}
