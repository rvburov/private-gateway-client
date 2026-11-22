package com.privategateway.client.vpn

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkInfo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import org.robolectric.shadows.ShadowNetwork
import org.robolectric.shadows.ShadowNetworkInfo
import java.net.InetAddress
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

@Suppress("DEPRECATION")
@RunWith(AndroidJUnit4::class)
class PhysicalProbeDnsTest {
    @Test
    fun `protected probe uses physical dns instead of the synthetic active vpn resolver`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val manager = context.getSystemService(ConnectivityManager::class.java)
        val shadow = shadowOf(manager)
        shadow.clearAllNetworks()
        val vpn = ShadowNetwork.newInstance(ConnectivityManager.TYPE_VPN)
        val wifi = ShadowNetwork.newInstance(ConnectivityManager.TYPE_WIFI)
        val vpnInfo = ShadowNetworkInfo.newInstance(NetworkInfo.DetailedState.CONNECTED, ConnectivityManager.TYPE_VPN, 0, true, NetworkInfo.State.CONNECTED)
        val wifiInfo = ShadowNetworkInfo.newInstance(NetworkInfo.DetailedState.CONNECTED, ConnectivityManager.TYPE_WIFI, 0, true, NetworkInfo.State.CONNECTED)
        shadow.addNetwork(vpn, vpnInfo)
        shadow.addNetwork(wifi, wifiInfo)
        shadow.setActiveNetworkInfo(vpnInfo)
        shadow.setNetworkCapabilities(vpn, NetworkCapabilities().also { shadowOf(it).addTransportType(NetworkCapabilities.TRANSPORT_VPN); shadowOf(it).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) })
        shadow.setNetworkCapabilities(wifi, NetworkCapabilities().also { shadowOf(it).addTransportType(NetworkCapabilities.TRANSPORT_WIFI); shadowOf(it).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) })
        shadow.setLinkProperties(vpn, LinkProperties().apply { interfaceName = "tun0"; setDnsServers(listOf(InetAddress.getByName("172.19.0.2"))) })
        shadow.setLinkProperties(wifi, LinkProperties().apply { interfaceName = "wlan0"; setDnsServers(listOf(InetAddress.getByName("192.0.2.53"))) })
        assertEquals(vpn, manager.activeNetwork)
        val dns = SystemNetworkDns(context)
        assertEquals(listOf("192.0.2.53"), dns.current())
        val before = dns.identity()
        shadow.setLinkProperties(wifi, LinkProperties().apply { interfaceName = "wlan0"; setDnsServers(listOf(InetAddress.getByName("198.51.100.53"))) })
        assertNotEquals(before, dns.identity())
    }
}
