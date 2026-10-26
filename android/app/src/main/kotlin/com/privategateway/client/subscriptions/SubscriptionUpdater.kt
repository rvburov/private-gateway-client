package com.privategateway.client.subscriptions

import android.util.Log
import com.privategateway.client.core.subscriptions.DownloadFailure
import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.FetchResult
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.subscriptions.SubscriptionFetch
import com.privategateway.client.core.subscriptions.SubscriptionSchedule
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.core.subscriptions.SubscriptionTimes
import com.privategateway.client.core.subscriptions.UpdateBlock
import com.privategateway.client.core.subscriptions.UpdateGate
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.vpn.TunnelMonitor
import com.privategateway.client.vpn.TunnelState
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/** Итог обновления подписки: новых и удаленных серверов, неудача с причиной или почему не начиналось. */
sealed interface UpdateOutcome {
    data class Updated(val added: Int, val removed: Int) : UpdateOutcome

    data class Failed(val reason: FailureReason) : UpdateOutcome

    data class Blocked(val block: UpdateBlock) : UpdateOutcome

    /** Подписку удалили, пока шло обновление. */
    data object Gone : UpdateOutcome
}

/** Что сообщить пользователю после обновления: выбранный сервер пропал или изменился; подписка по расписанию не обновилась. */
sealed interface SubscriptionNotice {
    val subscription: String

    data class Gone(val server: String, override val subscription: String) : SubscriptionNotice

    data class Changed(val server: String, override val subscription: String) : SubscriptionNotice

    data class Stale(override val subscription: String, val reason: FailureReason) : SubscriptionNotice
}

/**
 * Обновление подписок — `SubscriptionUpdater` десктопа. Загрузка — тем же разбором, что импорт; список меняет ApplyUpdate
 * (свое пользователя остается, выбранный пропавший — с пометкой); неудача оставляет прошлый список и запоминает причину.
 * С «только через VPN» адрес подписки уходит в сеть только через туннель: пока защита не подключена, ничего не грузится
 * (приложение не исключено из своего VPN — загрузка и правда идет через сервер).
 *
 * Когда: переход в «Защищено», каждые 10 минут, пока защита идет (или пока жив процесс, если «только через VPN»
 * выключено), смена настроек и открытие окна — по правилу «пора» (успех старше промежутка, неудача — не чаще раза в час).
 * Отказ по расписанию — сообщение «не обновились»; кнопкой — только итог на экране. В журнал — только числа и причина.
 */
@Singleton
class SubscriptionUpdater @Inject constructor(
    private val source: SubscriptionSource,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val monitor: TunnelMonitor,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val running = MutableStateFlow<Set<String>>(emptySet())
    private val events = MutableSharedFlow<SubscriptionNotice>(extraBufferCapacity = 16)
    private var started = false

    /** Подписки, которые обновляются сейчас, — для кнопки «Обновить» с кругом ожидания. */
    val busy: StateFlow<Set<String>> = running.asStateFlow()

    /** Сообщения для окна и уведомлений. */
    val notices: SharedFlow<SubscriptionNotice> = events.asSharedFlow()

    /** Проверки по расписанию: на переходе в «Защищено», при смене настроек и каждые 10 минут. Повторный вызов — ничего. */
    @Synchronized
    fun start() {
        if (started) {
            return
        }

        started = true
        val connected = monitor.status.map { it.state == TunnelState.Connected }.distinctUntilChanged()
        scope.launch {
            combine(connected, settings.subscriptions) { on, subs -> on to subs }.collectLatest {
                while (true) {
                    updateDue()
                    delay(CHECK_EVERY_MS)
                }
            }
        }
    }

    /** Окно открылось: проверить, не пора ли, — как по расписанию. */
    fun checkNow() {
        scope.launch { updateDue() }
    }

    /** Обновить подписки, которым пора; [scheduled] — сама, а не кнопкой: тогда неудача — сообщение «не обновились». */
    suspend fun updateDue(scheduled: Boolean = true) {
        val subscriptions = settings.subscriptions.first()
        if (UpdateGate.block(subscriptions.onlyViaVpn, connected()) != UpdateBlock.None) {
            return
        }

        val times = servers.groups.first().filter { it.canUpdate }.map { SubscriptionTimes(it.id, it.updatedAt, it.lastAttemptAt) }
        for (id in SubscriptionSchedule.due(times, subscriptions.refresh, System.currentTimeMillis())) {
            update(id, scheduled)
        }
    }

    /** Обновить одну подписку (кнопка «Обновить», новый адрес в «Изменить»). */
    suspend fun update(id: String, scheduled: Boolean = false): UpdateOutcome {
        val subscriptions = settings.subscriptions.first()
        val block = UpdateGate.block(subscriptions.onlyViaVpn, connected())
        if (block != UpdateBlock.None) {
            return UpdateOutcome.Blocked(block)
        }

        if (!claim(id)) {
            return UpdateOutcome.Blocked(UpdateBlock.Busy)
        }

        try {
            val name = servers.groups.first().firstOrNull { it.id == id }?.name ?: return UpdateOutcome.Gone
            val url = servers.subscriptionUrl(id) ?: return failed(id, name, DownloadFailure.NoAddress, null, scheduled)
            val fetched = withContext(Dispatchers.IO) {
                try {
                    SubscriptionFetch.fetch(url, source)
                } catch (e: CancellationException) {
                    throw e
                } catch (_: Exception) {
                    // Сбой вне загрузчика (сеть отвалилась на середине) — «ошибка сети», адреса в журнал не попадают.
                    FetchResult.Failed(DownloadFailure.Network)
                }
            }

            return when (val result = fetched) {
                is FetchResult.Failed -> failed(id, name, result.failure, result.status, scheduled)
                is FetchResult.Fetched -> {
                    val selected = settings.selectedServerId.first()
                    val update = servers.applyUpdate(id, result.servers, selected)
                    Log.i(TAG, "subscription: +${update.added} -${update.removed} ${update.selected}")
                    val server = update.selectedName
                    if (subscriptions.notifySelected && server != null) {
                        when (update.selected) {
                            SelectedChange.Missing -> events.tryEmit(SubscriptionNotice.Gone(server, name))
                            SelectedChange.Changed -> events.tryEmit(SubscriptionNotice.Changed(server, name))
                            SelectedChange.None -> Unit
                        }
                    }

                    UpdateOutcome.Updated(update.added, update.removed)
                }
            }
        } finally {
            synchronized(running) { running.value = running.value - id }
        }
    }

    /** Занять подписку; уже обновляется — false (проверка и отметка — одним шагом, двойное нажатие не грузит дважды). */
    private fun claim(id: String): Boolean = synchronized(running) {
        if (id in running.value) {
            false
        } else {
            running.value = running.value + id
            true
        }
    }

    private suspend fun failed(id: String, name: String, failure: DownloadFailure, status: Int?, scheduled: Boolean): UpdateOutcome {
        servers.markFailed(id, failure, status)
        Log.i(TAG, "subscription failed: $failure ${status ?: ""}".trim())
        val reason = FailureReason.of(failure, status)
        if (scheduled) {
            events.tryEmit(SubscriptionNotice.Stale(name, reason))
        }

        return UpdateOutcome.Failed(reason)
    }

    private fun connected() = monitor.status.value.state == TunnelState.Connected

    private companion object {
        const val TAG = "PrivateGateway"
        const val CHECK_EVERY_MS = 10L * 60 * 1000
    }
}
