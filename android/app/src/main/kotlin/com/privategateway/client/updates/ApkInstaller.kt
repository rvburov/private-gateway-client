package com.privategateway.client.updates

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import androidx.core.app.PendingIntentCompat
import androidx.core.content.IntentCompat
import com.privategateway.client.di.AppEntryPoint
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import javax.inject.Inject

/** Установщик Android для APK обновления — шов для тестов (в приложении — сессия [PackageInstaller]). */
interface ApkInstaller {
    /** Новая сессия установки на [size] байт; номер сессии. Не вышло — исключение. */
    fun open(size: Long): Int

    /**
     * Записать [apk] в сессию [id] и передать ее Android. Итог — в [UpdateInstallReceiver]: сначала, как правило, «нужно
     * подтверждение пользователя». Не вышло — исключение, сессия брошена.
     */
    fun commit(id: Int, apk: File)

    /** Бросить сессию (повторное нажатие или ошибка); ее уже нет — ничего. */
    fun abandon(id: Int)
}

/**
 * Установка обновления через сессию [PackageInstaller] (полная установка, только этот пакет): Android ставит APK поверх, только
 * если он подписан тем же ключом, и сам спрашивает пользователя. Итог сессии приходит изменяемым PendingIntent (Android
 * дописывает в него статус; неизменяемый у приложения с targetSdk 35+ он отвергает) в свой неэкспортируемый приемник.
 */
class PackageApkInstaller @Inject constructor(@param:ApplicationContext private val context: Context) : ApkInstaller {
    private val installer: PackageInstaller get() = context.packageManager.packageInstaller

    override fun open(size: Long): Int {
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            setSize(size)
            setInstallReason(PackageManager.INSTALL_REASON_USER)
        }
        return installer.createSession(params)
    }

    override fun commit(id: Int, apk: File) {
        try {
            installer.openSession(id).use { session ->
                apk.inputStream().use { input ->
                    session.openWrite(APK_NAME, 0, apk.length()).use { output ->
                        input.copyTo(output)
                        session.fsync(output)
                    }
                }

                val status = Intent(context, UpdateInstallReceiver::class.java).setAction(UpdateInstallReceiver.ACTION_STATUS)
                // Изменяемый: Android дописывает в него статус сессии и окно подтверждения.
                val pending = PendingIntentCompat.getBroadcast(context, id, status, 0, true)
                    ?: error("Нет PendingIntent для итога установки")
                session.commit(pending.intentSender)
            }
        } catch (e: Exception) {
            abandon(id)
            throw e
        }
    }

    override fun abandon(id: Int) {
        try {
            installer.abandonSession(id)
        } catch (_: SecurityException) {
            // Сессии уже нет (завершена или брошена) — бросать нечего.
        }
    }

    private companion object {
        const val APK_NAME = "base.apk"
    }
}

/**
 * Итог сессии установки обновления от Android: номер сессии, статус, окно подтверждения (у «нужно действие пользователя») и
 * текст ошибки — в [AppUpdates]. Приемник не экспортируется: присылает только PendingIntent приложения из [PackageApkInstaller].
 */
class UpdateInstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_STATUS) {
            return
        }

        EntryPointAccessors.fromApplication(context.applicationContext, AppEntryPoint::class.java).updates().installStatus(
            intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1),
            intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE),
            IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java),
            intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE),
        )
    }

    companion object {
        const val ACTION_STATUS = "com.privategateway.client.UPDATE_STATUS"
    }
}
