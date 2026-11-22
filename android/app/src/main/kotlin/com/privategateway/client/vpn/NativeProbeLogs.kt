package com.privategateway.client.vpn

import android.app.Application
import android.system.Os
import android.system.OsConstants
import com.privategateway.client.BuildConfig

/**
 * libXray pingBatch создает свое ядро без log-конфига вызывающего. Его stdout/stderr закрыты только в отдельном процессе:
 * произвольная ошибка native-builder не может записать extra-заголовок в Logcat до маскировки возвращенного ответа.
 */
internal object NativeProbeLogs {
    @Volatile private var quiet = false

    @Synchronized
    fun discard(): Boolean {
        if (Application.getProcessName() != "${BuildConfig.APPLICATION_ID}:latency") return false
        // gomobile во время первого запуска Go заменяет fd1/fd2 своими каналами Logcat. Проверяем повторно после init.
        if (quiet && runCatching { Os.readlink("/proc/self/fd/1") == "/dev/null" && Os.readlink("/proc/self/fd/2") == "/dev/null" }.getOrDefault(false)) return true
        quiet = runCatching {
            val sink = Os.open("/dev/null", OsConstants.O_WRONLY or OsConstants.O_CLOEXEC, 0)
            try {
                Os.dup2(sink, OsConstants.STDOUT_FILENO)
                Os.dup2(sink, OsConstants.STDERR_FILENO)
            } finally {
                Os.close(sink)
            }
            Os.readlink("/proc/self/fd/1") == "/dev/null" && Os.readlink("/proc/self/fd/2") == "/dev/null"
        }.getOrDefault(false)
        return quiet
    }

    fun ready(): Boolean = quiet
}
