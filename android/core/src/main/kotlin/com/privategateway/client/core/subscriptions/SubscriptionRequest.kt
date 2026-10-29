package com.privategateway.client.core.subscriptions

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import java.io.InterruptedIOException
import java.net.HttpURLConnection
import kotlin.coroutines.resumeWithException

/** Одна загрузка: отмена закрывает ее соединение, в том числе во время ожидания заголовков и перенаправления. */
class SubscriptionRequest {
    private val lock = Any()
    private var cancelled = false
    private var active: HttpURLConnection? = null
    private var stop: (() -> Unit)? = null

    fun onCancel(action: () -> Unit) {
        val alreadyCancelled = synchronized(lock) {
            stop = action
            cancelled
        }
        if (alreadyCancelled) action()
    }

    fun check() = synchronized(lock) {
        if (cancelled) throw InterruptedIOException("Загрузка отменена")
    }

    fun attach(connection: HttpURLConnection) = synchronized(lock) {
        if (cancelled) {
            connection.disconnect()
            throw InterruptedIOException("Загрузка отменена")
        }
        active = connection
    }

    fun detach(connection: HttpURLConnection) = synchronized(lock) {
        if (active === connection) active = null
    }

    fun cancel() {
        val (connection, action) = synchronized(lock) {
            cancelled = true
            (active to stop).also { active = null; stop = null }
        }
        connection?.disconnect()
        action?.invoke()
    }

    /** Блокирующий загрузчик — на IO, а отмена корутины немедленно закрывает HTTP, не ждет конца withContext(IO). */
    suspend fun execute(block: () -> SubscriptionContent): SubscriptionContent = suspendCancellableCoroutine { continuation ->
        continuation.invokeOnCancellation { cancel() }
        Dispatchers.IO.dispatch(continuation.context) {
            if (continuation.isActive) {
                try {
                    val value = block()
                    continuation.resumeWith(Result.success(value))
                } catch (e: Exception) {
                    continuation.resumeWithException(e)
                }
            }
        }
    }
}
