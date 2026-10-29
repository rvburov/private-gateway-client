package com.privategateway.client

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.debug.DebugProbes
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout

/** При исходном таймауте — состояния всех корутин и потоков: ожидание UI не показывает, где остановилась запись. */
fun <T> waitForAsync(block: suspend () -> T): T = runBlocking {
    try {
        withTimeout(10_000) { block() }
    } catch (failure: TimeoutCancellationException) {
        // Даже отказ диагностики не должен подменять исходную ошибку теста.
        runCatching { dumpAsyncTimeout() }
        throw failure
    }
}

@OptIn(ExperimentalCoroutinesApi::class)
private fun dumpAsyncTimeout() {
    System.err.println("Async test timeout: coroutine and thread states follow")
    runCatching { DebugProbes.dumpCoroutines(System.err) }.onFailure {
        System.err.println("Coroutine dump unavailable: ${it.javaClass.name}")
    }
    System.err.println()
    Thread.getAllStackTraces().entries.sortedBy { it.key.name }.forEach { (thread, frames) ->
        System.err.println("Thread ${thread.name}: ${thread.state}")
        frames.forEach { System.err.println("\tat $it") }
    }
}
