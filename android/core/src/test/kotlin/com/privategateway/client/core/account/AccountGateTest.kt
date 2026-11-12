package com.privategateway.client.core.account

import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Правило доступа (решение 618): отзыв — только 401, без ответа — последний подтвержденный срок. */
class AccountGateTest {
    private val now = Instant.parse("2026-11-12T10:00:00Z")
    private val later = now.plus(Duration.ofDays(30))
    private val earlier = now.minusSeconds(1)
    private val ok = StatusResult.Ok(later.plus(Duration.ofDays(1)), AccountUser("user@example.com", "Участник"), AccountDevice(1, "Pixel"))

    @Test
    fun `site confirms and extends`() {
        assertEquals(AccessVerdict.Keep(ok.expiresAt, confirmed = true), AccountGate.verdict(later, ok, now))
        assertEquals(AccessVerdict.Keep(ok.expiresAt, confirmed = true), AccountGate.verdict(earlier, ok, now), "подтверждение сайта главнее часов телефона")
    }

    @Test
    fun `only 401 revokes, even while the term is valid`() {
        assertEquals(AccessVerdict.Revoked, AccountGate.verdict(later, StatusResult.Revoked, now))
        assertEquals(AccessVerdict.Revoked, AccountGate.verdict(earlier, StatusResult.Revoked, now))
    }

    @Test
    fun `no answer keeps the confirmed term and expires after it`() {
        assertEquals(AccessVerdict.Keep(later, confirmed = false), AccountGate.verdict(later, StatusResult.Unreachable, now))
        assertEquals(AccessVerdict.Expired, AccountGate.verdict(earlier, StatusResult.Unreachable, now))
        assertEquals(AccessVerdict.Expired, AccountGate.verdict(now, StatusResult.Unreachable, now), "срок до, а не включая")
    }

    @Test
    fun `connecting needs a sign-in with a term in the future`() {
        assertFalse(AccountGate.allowed(null, now))
        assertFalse(AccountGate.allowed(earlier, now))
        assertFalse(AccountGate.allowed(now, now))
        assertTrue(AccountGate.allowed(later, now))
    }

    @Test
    fun `checks every 30 minutes`() {
        assertEquals(30 * 60_000L, AccountGate.CHECK_EVERY_MS)
    }
}
