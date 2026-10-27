package com.privategateway.client.notify

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationManagerCompat

/** Кнопка уведомления «Позже» (доска 22): убрать уведомление, ничего не меняя. */
class NoticeReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_LATER) {
            NotificationManagerCompat.from(context).cancel(intent.getStringExtra(EXTRA_TAG), ProtectionNotifier.ID)
        }
    }

    companion object {
        const val ACTION_LATER = "com.privategateway.client.NOTICE_LATER"
        const val EXTRA_TAG = "tag"
    }
}
