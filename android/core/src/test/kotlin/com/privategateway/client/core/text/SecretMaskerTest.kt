package com.privategateway.client.core.text

import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import kotlin.test.assertEquals

// Те же случаи, что у десктопа (SecretMaskerTests); значения заведомо ненастоящие.
class SecretMaskerTest {
    private val hidden = SecretMasker.HIDDEN

    @ParameterizedTest
    @ValueSource(
        strings = [
            "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=reality&pbk=Abc#NL",
            "VMESS://eyJhZGQiOiIyMDMuMC4xMTMuMjQifQ==",
            "trojan://password@203.0.113.24:443",
            "ss://YWVzLTI1Ni1nY206cGFzcw@203.0.113.24:8388",
            "hysteria2://pass@203.0.113.24:443?sni=example.com",
            "hy2://pass@203.0.113.24:443",
            "tuic://uuid:pass@203.0.113.24:443",
            "wireguard://key@203.0.113.24:51820",
        ],
    )
    fun `server link is hidden after scheme`(link: String) {
        val scheme = link.substringBefore("://")
        assertEquals("импорт $scheme://$hidden готов", SecretMasker.mask("импорт $link готов"))
    }

    @Test
    fun `subscription url keeps host and hides path and query`() {
        assertEquals("загрузка https://example.com/$hidden завершена", SecretMasker.mask("загрузка https://example.com/sub/AbCdEf123?token=xyz завершена"))
    }

    @Test
    fun `url without path is left as is`() {
        assertEquals("проверка https://example.com", SecretMasker.mask("проверка https://example.com"))
    }

    @Test
    fun `uuid is hidden`() {
        assertEquals("id=$hidden;", SecretMasker.mask("id=11111111-2222-3333-4444-55555555abcd;"))
    }

    @Test
    fun `base64 key is hidden`() {
        // Похоже на ключ WireGuard: 44 знака base64 с «=» в конце.
        assertEquals("peer $hidden ok", SecretMasker.mask("peer aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW3xY5zA7bC9d= ok"))
    }

    @ParameterizedTest
    @ValueSource(
        strings = [
            "/home/user/private-gateway-client/artifacts/bin/service",
            "служба запущена, ядро xray 25.10.15 готово",
            "0123456789abcdef0123456789abcdef0123456789abcdef",
            // Путь с цифрами и заглавными буквами похож на ключ, но начинается с «/».
            "журнал /Volumes/Backup2024/PrivateGatewayClient записан",
        ],
    )
    fun `ordinary text paths and hashes are not touched`(text: String) {
        assertEquals(text, SecretMasker.mask(text))
    }

    @Test
    fun `null and empty give empty string`() {
        assertEquals("", SecretMasker.mask(null))
        assertEquals("", SecretMasker.mask(""))
    }
}
