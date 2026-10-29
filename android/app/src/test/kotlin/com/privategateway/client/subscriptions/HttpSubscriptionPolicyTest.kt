package com.privategateway.client.subscriptions

import android.security.NetworkSecurityPolicy
import android.content.Context
import android.content.pm.ApplicationInfo
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class HttpSubscriptionPolicyTest {
    @Test
    fun `android permits the http subscription that the importer explicitly warns about`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        assertTrue(context.applicationInfo.flags and ApplicationInfo.FLAG_USES_CLEARTEXT_TRAFFIC != 0)
        assertTrue(NetworkSecurityPolicy.getInstance().isCleartextTrafficPermitted("example.org"))
    }
}
