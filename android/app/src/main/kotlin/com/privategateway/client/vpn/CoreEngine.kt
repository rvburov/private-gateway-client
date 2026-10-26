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
        val controller = object : DialerController {
            override fun protectFd(fd: Long): Boolean = protect(fd.toInt())
        }

        LibXray.registerDialerController(controller)
        try {
            LibXray.setDNS(controller, dns)
        } catch (e: Exception) {
            return "setDNS: ${e.message}"
        }

        val response = XrayResponse.parse(LibXray.invoke(XrayConfig.runRequest(configJson)))
        return if (response.success) null else response.error ?: "runXray"
    }

    override fun stop() {
        LibXray.invoke(XrayConfig.invoke("stopXray"))
        LibXray.resetDNS()
    }

    override fun version(): String? = try {
        JSONObject(LibXray.invoke(XrayConfig.invoke("xrayVersion"))).optJSONObject("data")?.optString("version")?.takeIf { it.isNotEmpty() }
    } catch (_: JSONException) {
        null
    }
}
