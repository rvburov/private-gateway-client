package com.privategateway.client

import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.io.PrintStream

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

private fun dumpAsyncTimeout() {
    System.err.println("Async test timeout: coroutine and thread states follow")
    runCatching {
        // Robolectric разделяет kotlin.* с родителем, но загружает свою копию kotlinx.coroutines.*.
        // События компилятора уже получает агент рядом с общими DebugProbesKt: дамп нужен из того же загрузчика.
        val hooks = Class.forName("kotlin.coroutines.jvm.internal.DebugProbesKt")
        val type = Class.forName("kotlinx.coroutines.debug.DebugProbes", true, hooks.classLoader)
        type.getMethod("dumpCoroutines", PrintStream::class.java).invoke(type.getField("INSTANCE").get(null), System.err)
    }.onFailure {
        System.err.println("Coroutine dump unavailable: ${it.javaClass.name}")
    }
    System.err.println()
    Thread.getAllStackTraces().entries.sortedBy { it.key.name }.forEach { (thread, frames) ->
        System.err.println("Thread ${thread.name}: ${thread.state}")
        frames.forEach { System.err.println("\tat $it") }
    }
}
