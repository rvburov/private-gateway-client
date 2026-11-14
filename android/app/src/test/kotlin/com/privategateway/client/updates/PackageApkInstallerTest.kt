package com.privategateway.client.updates

import android.content.Context
import android.content.pm.PackageInstaller
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Восстановление опирается на собственные сессии PackageInstaller, а не только на число из настроек. */
@RunWith(AndroidJUnit4::class)
@Config(sdk = [28, 31, 35])
class PackageApkInstallerTest {
    @Test
    fun `only live own sessions targeting this app can be restored`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val bridge = PackageApkInstaller(context)
        val installer = context.packageManager.packageInstaller
        val own = bridge.open(128)
        val anotherPackage = installer.createSession(PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName("com.example.another")
        })
        try {
            assertTrue(bridge.owns(own))
            assertFalse(bridge.owns(anotherPackage), "даже собственная сессия другого пакета не является обновлением клиента")
            assertFalse(bridge.owns(Int.MAX_VALUE))
            bridge.abandon(own)
            assertFalse(bridge.owns(own))
        } finally {
            installer.abandonSession(anotherPackage)
        }
    }
}
