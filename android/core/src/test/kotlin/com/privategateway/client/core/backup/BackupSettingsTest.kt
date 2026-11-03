package com.privategateway.client.core.backup

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals

/** Копия iPhone на Android (DECISIONS.md, п. 3): общий эталон `shared/testdata/backup`, значения списков — без учета регистра. */
class BackupSettingsTest {
    private val fixtures = File(System.getProperty("testdata") ?: "../../shared/testdata", "backup")

    @Test
    fun iphone_copy_restores_every_choice_on_android() {
        val copy = MobileBackup.read(File(fixtures, "mobile-ios.plain.json").readBytes())
        val android = MobileBackup.read(File(fixtures, "mobile-android.plain.json").readBytes())

        assertEquals(android.settings, BackupSettings.normalize(copy.settings!!), "значения iPhone приводятся к именам Android")
        assertEquals("Custom", BackupSettings.normalize(mapOf("tunnel_dns" to "custom"))["tunnel_dns"])
        assertEquals("Every6Hours", BackupSettings.normalize(mapOf("subs_refresh" to "every6hours"))["subs_refresh"])
    }

    @Test
    fun unknown_values_and_other_keys_stay_as_they_are() {
        val values = mapOf("tunnel_dns" to "Другой", "tunnel_custom_dns" to "custom", "tunnel_mtu" to "1360")
        assertEquals(values, BackupSettings.normalize(values), "неизвестное — как есть; не списки — не трогаются")
    }
}
