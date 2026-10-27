package com.privategateway.client.notify

import android.Manifest
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.privategateway.client.MainActivity
import com.privategateway.client.PrivateGatewayApp
import com.privategateway.client.R
import com.privategateway.client.core.notify.Notice
import com.privategateway.client.core.notify.NoticeAction
import com.privategateway.client.core.notify.NoticeDesk
import com.privategateway.client.core.notify.NoticeKind
import com.privategateway.client.core.notify.NotificationPolicy
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.subscriptions.SubscriptionNotice
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.ui.Texts
import com.privategateway.client.vpn.GatewayVpnService
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Уведомления о защите и подписках (доска 22) — NotificationCenter десктопа: смены состояния туннеля
 * ([NotificationPolicy.forChange]) и события подписок; что показывать — [NoticeDesk] (повтор, окно на экране, уровень из
 * настроек, снятие прошедшей беды). Важные — каналом «Сбои защиты», обычные — «События». Работает в процессе приложения
 * и без окна: службу запускает и «Постоянная VPN». Имена серверов и подписок — только в разблокированной шторке.
 */
@Singleton
class ProtectionNotifier @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val monitor: TunnelMonitor,
    private val servers: ServerRepository,
    private val settings: SettingsRepository,
    private val updater: SubscriptionUpdater,
    private val window: AppWindow,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val lock = Mutex()
    private val desk = NoticeDesk()
    private var started = false

    /** Слушать туннель и подписки. Первое состояние — точка отсчета: о том, что было до запуска, уведомлений нет. */
    @Synchronized
    fun start() {
        if (started) {
            return
        }

        started = true
        scope.launch {
            var before = monitor.status.value
            monitor.status.collect { after ->
                val previous = before
                before = after
                val server = name(after.serverId ?: previous.serverId)
                NotificationPolicy.forChange(previous, after, server, after.lockdown ?: previous.lockdown ?: false)?.let { deliver(it) }
            }
        }

        scope.launch {
            updater.notices.collect { notice ->
                when (notice) {
                    is SubscriptionNotice.Gone -> NotificationPolicy.forSelected(SelectedChange.Missing, notice.server, notice.subscription)
                    is SubscriptionNotice.Changed -> NotificationPolicy.forSelected(SelectedChange.Changed, notice.server, notice.subscription)
                    is SubscriptionNotice.Stale -> NotificationPolicy.forStale(notice.subscription, notice.reason)
                }?.let { deliver(it) }
            }
        }
    }

    /** Текст уведомления: {0} — сервер, {1} — подписка, {2} — причина словами и код или почему подписка не загрузилась. */
    fun text(notice: Notice): String? {
        val id = NoticeTexts.text(notice.textKey) ?: return null
        val code = notice.code
        val failure = notice.failure
        val reason = when {
            code != null -> context.getString(Texts.error(code)) + " · " + code
            failure != null -> Texts.failure(failure).let { (text, arg) -> if (arg == null) context.getString(text) else context.getString(text, arg) }
            else -> ""
        }

        return context.getString(id, notice.server ?: context.getString(R.string.notify_server_none), notice.subscription.orEmpty(), reason)
    }

    private suspend fun name(id: String?): String? = id?.let { servers.servers.first().firstOrNull { item -> item.id == it }?.displayName }

    private suspend fun deliver(notice: Notice) {
        lock.withLock {
            val delivery = desk.deliver(notice, SystemClock.elapsedRealtime(), window.visible.value, settings.notifications.first())
            if (delivery.withdrawProtection) {
                NotificationManagerCompat.from(context).cancel(Notice.PROTECTION_TAG, ID)
            }

            delivery.show?.let { show(it) }
        }
    }

    private fun show(notice: Notice) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val text = text(notice) ?: return
        val title = context.getString(NoticeTexts.title(notice.kind))
        val channel = if (notice.important) PrivateGatewayApp.CHANNEL_ALERTS else PrivateGatewayApp.CHANNEL_EVENTS
        // На экране блокировки — только заголовок: в тексте названия сервера и подписки.
        val lockScreen = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(title)
            .build()
        val builder = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.pg_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(intent(tap(notice), notice))
            .setAutoCancel(true)
            .setCategory(if (notice.important) NotificationCompat.CATEGORY_ERROR else NotificationCompat.CATEGORY_STATUS)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(lockScreen)
        notice.actions.forEach { builder.addAction(0, context.getString(NoticeTexts.action(it)), intent(it, notice)) }
        NotificationManagerCompat.from(context).notify(notice.tag, ID, builder.build())
    }

    /** Нажатие на само уведомление: о защите — окно, о сервере подписки — список серверов, о неудаче — «Профили и подписки». */
    private fun tap(notice: Notice): NoticeAction = when (notice.kind) {
        NoticeKind.ServerGone, NoticeKind.ServerChanged -> NoticeAction.ChooseServer
        NoticeKind.ServersNotUpdated -> NoticeAction.OpenSubscriptions
        else -> NoticeAction.Open
    }

    private fun intent(action: NoticeAction, notice: Notice): PendingIntent {
        // Свой номер у каждой пары «метка — кнопка»: иначе Android отдал бы одной кнопке PendingIntent другой.
        val code = notice.tag.hashCode() * 31 + action.ordinal
        return when (action) {
            NoticeAction.Open -> activity(null, code)
            NoticeAction.ChangeServer, NoticeAction.ChooseServer, NoticeAction.ChooseOther -> activity(MainActivity.TAB_SERVERS, code)
            NoticeAction.OpenSubscriptions -> activity(MainActivity.TAB_SUBSCRIPTIONS, code)
            NoticeAction.Retry, NoticeAction.Connect -> PendingIntent.getForegroundService(
                context,
                code,
                Intent(context, GatewayVpnService::class.java).setAction(GatewayVpnService.ACTION_CONNECT),
                FLAGS,
            )

            NoticeAction.Later -> PendingIntent.getBroadcast(
                context,
                code,
                Intent(context, NoticeReceiver::class.java).setAction(NoticeReceiver.ACTION_LATER).putExtra(NoticeReceiver.EXTRA_TAG, notice.tag),
                FLAGS,
            )
        }
    }

    private fun activity(tab: String?, code: Int): PendingIntent = PendingIntent.getActivity(
        context,
        code,
        Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .apply { tab?.let { putExtra(MainActivity.EXTRA_TAB, it) } },
        FLAGS,
    )

    companion object {
        /** Номер уведомлений: их различает метка (Notice.tag) — о защите одно, у каждой подписки свое. */
        const val ID = 10
        private const val FLAGS = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
    }
}
