package com.privategateway.client

import com.privategateway.client.core.account.AccountDevice
import com.privategateway.client.core.account.AccountSession
import com.privategateway.client.core.account.AccountUser
import com.privategateway.client.di.AppEntryPoint
import java.time.Duration
import java.time.Instant

/**
 * Вход в аккаунт сайта для проверок на устройстве (решение 623): без входа окно показывает экран входа, а служба не
 * поднимает туннель. Демо-режима у телефона нет, настоящий сайт поддельный токен не примет — поэтому вход засевается,
 * и сайт его не проверяет. Почта и токен — заведомо ненастоящие.
 */
object TestAccount {
    suspend fun seed(app: AppEntryPoint) {
        app.account().seedForTests(
            AccountSession(
                token = "android-test-token",
                expiresAt = Instant.now().plus(Duration.ofDays(1)),
                user = AccountUser("user@example.com", "Android test"),
                device = AccountDevice(1, "Emulator"),
            ),
        )
    }
}
