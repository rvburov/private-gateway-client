package com.privategateway.client.data

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ResolveInfo
import android.os.Build
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.Collator
import javax.inject.Inject
import javax.inject.Singleton

/** Программа телефона для «Программы мимо VPN»: имя пакета и название, как в списке программ Android. */
data class PhoneApp(val packageName: String, val label: String)

/**
 * Программы телефона, которые можно пустить мимо VPN (доска 13), — те, что видны в списке программ (есть значок запуска):
 * так их находит и сам пользователь. Самого приложения в списке нет: через его туннель идут проба связи и подписки. Видеть
 * чужие программы с Android 11 позволяет запрос `<queries>` по значку запуска в манифесте — без QUERY_ALL_PACKAGES.
 */
@Singleton
class PhoneApps @Inject constructor(@ApplicationContext private val context: Context) {
    /** Программы по названию (по правилам языка телефона); читаются с диска — не в главном потоке. */
    suspend fun launchable(): List<PhoneApp> = withContext(Dispatchers.IO) {
        val manager = context.packageManager
        val collator = Collator.getInstance()
        launchers(manager)
            .map { it.activityInfo.applicationInfo }
            .filter { it.packageName != context.packageName }
            .distinctBy { it.packageName }
            .map { info -> PhoneApp(info.packageName, manager.getApplicationLabel(info).toString().ifBlank { info.packageName }) }
            .sortedWith(compareBy<PhoneApp, String>(collator) { it.label })
    }

    private fun launchers(manager: PackageManager): List<ResolveInfo> {
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            manager.queryIntentActivities(intent, PackageManager.ResolveInfoFlags.of(0L))
        } else {
            legacyLaunchers(manager, intent)
        }
    }

    /** До Android 13 флаги — числом. */
    @Suppress("DEPRECATION")
    private fun legacyLaunchers(manager: PackageManager, intent: Intent): List<ResolveInfo> = manager.queryIntentActivities(intent, 0)
}
