package com.privategateway.client.vpn

import android.app.Notification
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.privategateway.client.R
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import kotlin.test.assertEquals
import kotlin.test.assertFalse

@RunWith(AndroidJUnit4::class)
class TunnelNotificationTest {
    @Test
    fun `the lock screen version of a running tunnel never contains its server`() {
        // onCreate не нужен: проверяем собранное настоящее уведомление без запуска VPN и внедрения ядра.
        val service = Robolectric.buildService(GatewayVpnService::class.java).get()
        val method = GatewayVpnService::class.java.getDeclaredMethod("notification", String::class.java, String::class.java, Long::class.javaObjectType)
        method.isAccessible = true
        val notification = method.invoke(service, "IP скрыт · Секретный сервер", "Шифруется", 1000L) as Notification
        assertEquals(service.getString(R.string.status_connected_title), notification.publicVersion.extras.getString(Notification.EXTRA_TITLE))
        assertFalse(notification.publicVersion.extras.toString().contains("Секретный сервер"))
    }
}
