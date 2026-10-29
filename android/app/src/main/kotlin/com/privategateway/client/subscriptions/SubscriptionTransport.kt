package com.privategateway.client.subscriptions

import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.DownloadLimits
import com.privategateway.client.core.subscriptions.SubscriptionContent
import com.privategateway.client.core.subscriptions.SubscriptionDownloadException
import com.privategateway.client.core.subscriptions.SubscriptionDownloader
import com.privategateway.client.core.subscriptions.ProtectedSubscriptionDownloader
import com.privategateway.client.core.subscriptions.SubscriptionRequest
import com.privategateway.client.vpn.OwnVpnNetwork
import java.net.URI
import javax.inject.Inject
import kotlinx.coroutines.withTimeoutOrNull

/** Одна загрузка с выбранным путем: только наша VPN либо обычная сеть по явной настройке пользователя. */
fun interface SubscriptionTransport {
    suspend fun download(url: URI, onlyViaVpn: Boolean): SubscriptionContent
}

class DeviceSubscriptionTransport @Inject constructor(private val vpn: OwnVpnNetwork) : SubscriptionTransport {
    override suspend fun download(url: URI, onlyViaVpn: Boolean): SubscriptionContent {
        val request = SubscriptionRequest()
        try {
            return withTimeoutOrNull(DownloadLimits.TIMEOUT_MS.toLong()) {
                val downloader = if (onlyViaVpn) {
                    val captured = vpn.capture(request)
                    ProtectedSubscriptionDownloader(captured.proxy, captured.sockets, request, captured.valid)
                } else {
                    SubscriptionDownloader(request = request)
                }
                request.execute { downloader.download(url) }
            } ?: throw SubscriptionDownloadException(DownloadFailure.Timeout)
        } finally {
            request.cancel()
            vpn.release(request)
        }
    }
}
