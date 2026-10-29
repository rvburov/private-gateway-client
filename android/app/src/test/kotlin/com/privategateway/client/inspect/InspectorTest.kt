package com.privategateway.client.inspect

import android.content.Context
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.lifecycle.viewModelScope
import com.privategateway.client.R
import com.privategateway.client.core.inspect.AddressFailure
import com.privategateway.client.core.inspect.AddressInfo
import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind
import com.privategateway.client.core.inspect.ProbeKind
import com.privategateway.client.core.inspect.ProbeOutcome
import com.privategateway.client.core.inspect.ProbeResult
import com.privategateway.client.core.inspect.ProbeRunner
import com.privategateway.client.core.inspect.ProbeStep
import com.privategateway.client.core.inspect.PublicAddress
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import com.privategateway.client.ui.check.InspectorActions
import com.privategateway.client.ui.check.InspectorSection
import com.privategateway.client.ui.theme.PgTheme
import com.privategateway.client.vpn.TunnelMonitor
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.job
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.net.InetAddress
import java.net.UnknownHostException
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Инспектор сети Android без устройства: модель окна с подставными сетью и ответами и то, что видно на «Проверке». */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(AndroidJUnit4::class)
@Config(qualifiers = "w384dp-h832dp")
class InspectorTest {
    @get:Rule
    val compose = createComposeRule()

    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val monitor = TunnelMonitor()
    private val asked = CopyOnWriteArrayList<String>()
    private var answers: (String) -> String? = { null }

    private val wifi = PhoneNetwork(
        PhoneNetworkKind.Wifi, "wlan0", isDefault = true, addresses = listOf("192.0.2.23/24", "2001:db8::23/64"), gateways = listOf("192.0.2.1"),
        dns = listOf("192.0.2.1"), privateDns = "", mtu = 1500, metered = false, validated = true, captive = false,
    )

    private class Networks(list: List<PhoneNetwork>) : PhoneNetworkSource {
        var started = 0
        override val networks: StateFlow<List<PhoneNetwork>> = MutableStateFlow(list)

        override fun start() {
            started++
        }
    }

    private val networks = Networks(listOf(wifi))

    private val modelLazy = lazy {
        val names = mapOf("example.org" to listOf(InetAddress.getByName("203.0.113.10")))
        val web = object : WebFetch {
            override suspend fun get(url: String): String? {
                asked += url
                return answers(url)
            }
        }
        InspectorViewModel(
            networks,
            web,
            ProbeRunner(resolve = { names[it] ?: throw UnknownHostException(it) }),
            monitor,
        )
    }

    private val model by modelLazy

    // Главный поток Robolectric занят ожиданием теста: viewModelScope (Dispatchers.Main) идет без очереди Looper.
    @Before
    fun main() = Dispatchers.setMain(UnconfinedTestDispatcher())

    @After
    fun reset() {
        // stateIn(Eagerly) живет в scope модели. Закрываем его до замены общего Main, но не создаем ленивую модель
        // ради cleanup в проверках, которым нужны только компоненты Compose.
        if (modelLazy.isInitialized()) {
            runBlocking { model.viewModelScope.coroutineContext.job.cancelAndJoin() }
        }
        Dispatchers.resetMain()
    }

    private fun <T> waitFor(read: () -> T, done: (T) -> Boolean): T = runBlocking {
        withTimeout(10_000) {
            while (!done(read())) {
                delay(20)
            }

            read()
        }
    }

    @Test
    fun `networks are read once and ordered`() {
        val ui = waitFor({ model.ui.value }) { it.networks.isNotEmpty() }
        assertEquals(listOf(wifi), ui.networks)
        assertEquals(1, networks.started)
    }

    @Test
    fun `external ip says whether it went through the server`() {
        answers = { "fl=1\nip=203.0.113.24\n" }
        model.measureIp()
        val direct = waitFor({ model.ui.value.publicIp }) { !it.measuring && it.address != null }
        assertEquals("203.0.113.24", direct.address)
        assertEquals(false, direct.viaServer)
        assertEquals(listOf(PublicAddress.TRACE), asked)

        monitor.set(TunnelStatus(TunnelState.Connected, serverId = "nl"))
        model.measureIp()
        assertTrue(waitFor({ model.ui.value.publicIp }) { !it.measuring && it.address != null }.viaServer, "под защитой — адрес выхода сервера")

        answers = { null }
        model.measureIp()
        assertTrue(waitFor({ model.ui.value.publicIp }) { !it.measuring && (it.failed || it.address == null) }.failed)
    }

    @Test
    fun `provider is asked only about a public address`() {
        answers = { "ip=192.0.2.23\n" }
        model.measureIp()
        waitFor({ model.ui.value.publicIp }) { it.address != null }
        asked.clear()
        model.describe()
        val private = waitFor({ model.ui.value.publicIp }) { it.info != null }.info
        assertEquals(AddressFailure.Private, private?.failure)
        assertEquals(emptyList(), asked, "о частном адресе не спрашивают")

        answers = { url ->
            when {
                url.endsWith("/cdn-cgi/trace") -> "ip=1.1.1.1\n"
                url.contains("prefix-overview") -> """{"status":"ok","data":{"announced":true,"asns":[{"asn":64500,"holder":"EXAMPLE-AS"}],"resource":"1.1.1.0/24"}}"""
                else -> """{"status":"ok","data":{"located_resources":[{"locations":[{"country":"AU","city":"Sydney"}]}]}}"""
            }
        }
        model.measureIp()
        waitFor({ model.ui.value.publicIp }) { it.address == "1.1.1.1" }
        model.describe()
        val info = assertNotNull(waitFor({ model.ui.value.publicIp }) { it.info != null }.info)
        assertEquals(AddressInfo("1.1.1.1", 64500, "EXAMPLE-AS", "1.1.1.0/24", "AU", "Sydney", null), info)
    }

    @Test
    fun `probe checks the host and the port before running`() {
        model.target("example.com; reboot")
        model.run()
        assertEquals(R.string.probe_error_target, waitFor({ model.ui.value.probe }) { it.error != null }.error)

        model.kind(ProbeKind.Tcp)
        model.target("example.org")
        model.port("70000")
        model.run()
        assertEquals(R.string.probe_error_port, waitFor({ model.ui.value.probe }) { it.error != null }.error)

        model.kind(ProbeKind.Dns)
        model.run()
        val result = assertNotNull(waitFor({ model.ui.value.probe }) { !it.running && it.result != null }.result)
        assertEquals(listOf("203.0.113.10"), result.steps.map { it.address })
        assertNull(model.ui.value.probe.error)
    }

    @Test
    fun `section shows the networks the address the provider and the probe`() {
        val ui = InspectorUi(
            networks = listOf(wifi),
            publicIp = PublicIpUi(address = "203.0.113.24", viaServer = true, info = AddressInfo("203.0.113.24", 64500, "EXAMPLE-AS", "203.0.113.0/24", "NL", null, null)),
            probe = ProbeUi(kind = ProbeKind.Tcp, target = "example.org", result = ProbeResult(ProbeKind.Tcp, "example.org", 443, listOf(ProbeStep(1, "203.0.113.10", 42, ProbeOutcome.Ok)))),
        )
        var measured = 0
        compose.setContent {
            PgTheme {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    InspectorSection(ui, protected = true, InspectorActions(measureIp = { measured++ }))
                }
            }
        }

        compose.onNodeWithText(context.getString(R.string.network_kind_wifi) + " · wlan0").assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.android_inspect_default)).assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.android_inspect_privateDns_auto)).assertIsDisplayed()
        compose.onNodeWithTag("inspect.ip.address").performScrollTo().assertTextContains("203.0.113.24")
        compose.onNodeWithText(context.getString(R.string.network_public_exit)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("AS64500 · EXAMPLE-AS").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText(context.getString(R.string.probe_path_vpn)).performScrollTo().assertIsDisplayed()
        compose.onNodeWithTag("inspect.probe.summary").performScrollTo().assertTextContains(context.getString(R.string.probe_summary_tcp, "443", "42"))
        compose.onNodeWithTag("inspect.ip.measure").performScrollTo().performClick()
        assertEquals(1, measured)
    }

    @Test
    fun `phone networks come from the reader started once`() = runBlocking<Unit> {
        val reader = PhoneNetworkReader(context)
        reader.start()
        reader.start()
        // В Robolectric сетей Android не сообщает сам — достаточно, что подписка прошла и поток живой.
        reader.networks.first()
    }
}
