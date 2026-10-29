package com.privategateway.client

import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.PrintStream
import kotlin.test.assertIs
import kotlin.test.assertSame
import kotlin.test.assertTrue

/** Агент находится вне песочницы Robolectric; проверяется стек настоящей корутины внутри нее. */
@RunWith(AndroidJUnit4::class)
class AsyncTestWaitTest {
    private val entered = CompletableDeferred<Unit>()

    private suspend fun suspensionSentinel() {
        entered.complete(Unit)
        try {
            awaitCancellation()
        } finally {
            // Нужна собственная точка приостановки в стеке, а не оптимизированный хвостовой вызов awaitCancellation.
            entered.cancel()
        }
    }

    @Test
    fun `timeout dumps a suspended coroutine from the same Robolectric sandbox`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())
        val output = ByteArrayOutputStream()
        val previous = System.err
        var original: TimeoutCancellationException? = null
        val failure: Throwable?
        try {
            // launch вернется только после реального awaitCancellation: готовность не зависит от задержки таймера.
            scope.launch(start = CoroutineStart.UNDISPATCHED) { suspensionSentinel() }
            withTimeout(2_000) { entered.await() }
            PrintStream(output).use { stream ->
                System.setErr(stream)
                failure = runCatching {
                    waitForAsync {
                        try {
                            withTimeout(25) { awaitCancellation() }
                        } catch (timeout: TimeoutCancellationException) {
                            original = timeout
                            throw timeout
                        }
                    }
                }.exceptionOrNull()
            }
        } finally {
            System.setErr(previous)
            scope.coroutineContext.job.cancelAndJoin()
        }
        assertIs<TimeoutCancellationException>(failure)
        // При -ea coroutines восстанавливает стек копией ошибки: исходная причина остается прежней.
        assertSame(rootCause(original!!), rootCause(failure))
        val dump = output.toString()
        assertTrue(dump.split("\n\n").any { it.contains("SUSPENDED") && it.contains("suspensionSentinel") }, dump)
        assertTrue(dump.contains("Thread "), dump)
    }

    private fun rootCause(failure: Throwable): Throwable {
        var result = failure
        while (result.cause is TimeoutCancellationException && result.cause !== result) {
            result = result.cause!!
        }
        return result
    }
}
