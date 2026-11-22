package com.privategateway.client.vpn

import android.app.Service
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Binder
import android.os.IBinder
import android.os.Parcel
import android.os.ParcelFileDescriptor
import android.os.Process
import com.privategateway.client.core.vpn.TunnelState
import dagger.hilt.android.qualifiers.ApplicationContext
import libXray.DialerController
import libXray.LibXray
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import javax.inject.Inject
import javax.inject.Singleton

/** Один ограниченный запрос замера. Реализация работает в отдельном процессе, даже когда VPN выключен. */
fun interface LatencyWorker {
    fun ping(request: String, dns: String): String?

    /** Отменить только свой отдельный процесс замера; основное ядро не затрагивается. */
    fun cancel() = Unit
}

/**
 * API libXray хранит ядро, DNS и контроллер сокетов в глобальных переменных процесса. Второе ядро поэтому запускается
 * отдельно. Дескриптор каждого его сокета передается действующей службе, а не подменяется номером из другого процесса.
 * Это разрешает только соединения замера; маршруты и блок при обрыве не меняются.
 */
class LatencyProbeService : Service() {
    private val binder = object : Binder() {
        override fun onTransact(code: Int, data: Parcel, reply: Parcel?, flags: Int): Boolean {
            if (code == CANCEL) {
                require(Binder.getCallingUid() == Process.myUid())
                data.enforceInterface(DESCRIPTOR)
                val expected = data.readLong()
                cancelled.accumulateAndGet(expected) { before, next -> maxOf(before, next) }
                if (expected != 0L && expected == nonce && android.app.Application.getProcessName() == "$packageName:latency") {
                    Process.killProcess(Process.myPid())
                }
                return true
            }
            if (code != MEASURE) return super.onTransact(code, data, reply, flags)
            require(Binder.getCallingUid() == Process.myUid())
            data.enforceInterface(DESCRIPTOR)
            val call = data.readLong()
            if (call <= 0) return false
            val request = data.readString() ?: return false
            val dns = data.readString() ?: return false
            val callback = data.readStrongBinder() ?: return false
            if (request.length > MAX_REQUEST || dns.length > 256) return false
            if (!NativeProbeLogs.ready()) {
                reply?.writeString(null)
                return true
            }
            synchronized(lock) {
                nonce = call
                if (call <= cancelled.get()) {
                    nonce = 0
                    reply?.writeString(null)
                    return true
                }
                if (!registered) {
                    LibXray.registerDialerController(controller)
                    registered = true
                }
                if (!NativeProbeLogs.discard()) {
                    nonce = 0
                    reply?.writeString(null)
                    return true
                }
                protector = callback
                try {
                    LibXray.setDNS(controller, dns)
                    reply?.writeString(NativeProbeResponse.safe(LibXray.invoke(request)))
                } catch (_: Exception) {
                    reply?.writeString(null)
                } finally {
                    LibXray.resetDNS()
                    if (nonce == call && protector == callback) {
                        protector = null
                        nonce = 0
                    }
                }
            }
            return true
        }
    }

    // onDestroy bound-службы не сбрасывает процессный контроллер: следующая порция может уже создавать новую службу.
    override fun onBind(intent: Intent?): IBinder = binder

    companion object {
        const val DESCRIPTOR = "com.privategateway.client.LatencyProbe"
        const val MEASURE = IBinder.FIRST_CALL_TRANSACTION
        const val PROTECT = IBinder.FIRST_CALL_TRANSACTION + 1
        const val CANCEL = IBinder.FIRST_CALL_TRANSACTION + 2
        const val MAX_REQUEST = 256 * 1024

        // Go хранит контроллеры до конца процесса, а bound-служба создается заново между порциями.
        // Контроллер и его текущий callback поэтому общие для всех экземпляров этой службы.
        private val lock = Any()
        @Volatile private var protector: IBinder? = null
        private var registered = false
        @Volatile private var nonce = 0L
        private val cancelled = AtomicLong()
        private val controller = object : DialerController {
            override fun protectFd(fd: Long): Boolean {
                val remote = protector ?: return false
                return runCatching {
                    ParcelFileDescriptor.fromFd(fd.toInt()).use { socket ->
                        val input = Parcel.obtain()
                        val output = Parcel.obtain()
                        try {
                            input.writeInterfaceToken(DESCRIPTOR)
                            input.writeParcelable(socket, 0)
                            remote.transact(PROTECT, input, output, 0) && output.readInt() == 1
                        } finally {
                            input.recycle()
                            output.recycle()
                        }
                    }
                }.getOrDefault(false)
            }
        }
    }
}

/** Привязка живет одну порцию: сбой процесса дает ошибку ядра, а не ложный статус недоступности всех серверов. */
@Singleton
class IsolatedLatencyWorker @Inject constructor(
    @param:ApplicationContext private val context: Context,
    private val engine: CoreEngine,
    private val monitor: TunnelMonitor,
) : LatencyWorker {
    private data class Active(val binder: IBinder, val nonce: Long)
    private val active = AtomicReference<Active?>(null)
    private val serial = AtomicLong(android.os.SystemClock.elapsedRealtimeNanos())
    private val cancellation = AtomicLong()
    private val requests = Any()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun cancel() {
        cancelCall(serial.get())
    }

    private fun cancelCall(expected: Long) {
        val current = synchronized(requests) {
            if (serial.get() != expected) return
            cancellation.incrementAndGet()
            active.get()?.takeIf { it.nonce == expected }
        } ?: return
        val input = Parcel.obtain()
        val output = Parcel.obtain()
        try {
            input.writeInterfaceToken(LatencyProbeService.DESCRIPTOR)
            input.writeLong(current.nonce)
            runCatching { current.binder.transact(LatencyProbeService.CANCEL, input, output, 0) }
        } finally {
            input.recycle()
            output.recycle()
        }
    }

    override fun ping(request: String, dns: String): String? {
        val (call, epoch) = synchronized(requests) { serial.incrementAndGet() to cancellation.get() }
        val session = monitor.status.value
        val network = SystemNetworkDns(context).identity()
        val ready = CountDownLatch(1)
        val remote = AtomicReference<IBinder?>(null)
        val connection = object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName, service: IBinder) {
                remote.set(service)
                ready.countDown()
            }

            override fun onServiceDisconnected(name: ComponentName) {
                remote.set(null)
            }

            override fun onNullBinding(name: ComponentName) = ready.countDown()

            override fun onBindingDied(name: ComponentName) = ready.countDown()
        }
        val callback = object : Binder() {
            @Suppress("DEPRECATION")
            override fun onTransact(code: Int, data: Parcel, reply: Parcel?, flags: Int): Boolean {
                if (code != LatencyProbeService.PROTECT) return super.onTransact(code, data, reply, flags)
                require(Binder.getCallingUid() == Process.myUid())
                data.enforceInterface(LatencyProbeService.DESCRIPTOR)
                val socket = data.readParcelable<ParcelFileDescriptor>(ParcelFileDescriptor::class.java.classLoader) ?: return false
                socket.use {
                    val current = monitor.status.value
                    val same = current.state == session.state && current.serverId == session.serverId && current.since == session.since &&
                        current.offline == session.offline && SystemNetworkDns(context).identity() == network
                    if (!same) {
                        reply?.writeInt(0)
                        return true
                    }
                    // Подключение уже могло начаться: сначала действующий protect, затем разрешение лишь для Off/Error.
                    val protected = engine.protectSocket(it.fd)
                    val state = monitor.status.value.state
                    val allowed = protected || (state in listOf(TunnelState.Off, TunnelState.Error) && !monitor.blocked.value)
                    val boundToPhysical = allowed && SystemNetworkDns(context).bind(it.fileDescriptor)
                    reply?.writeInt(if (boundToPhysical) 1 else 0)
                }
                return true
            }
        }
        val bound = context.bindService(Intent(context, LatencyProbeService::class.java), connection, Context.BIND_AUTO_CREATE)
        if (!bound) return null
        val watch = scope.launch {
            while (isActive) {
                delay(100)
                val now = monitor.status.value
                if (now.state != session.state || now.serverId != session.serverId || now.since != session.since || now.offline != session.offline || SystemNetworkDns(context).identity() != network) {
                    cancelCall(call)
                    return@launch
                }
            }
        }
        return try {
            if (!ready.await(5, TimeUnit.SECONDS)) return null
            if (cancellation.get() != epoch) return null
            val worker = remote.get() ?: return null
            active.set(Active(worker, call))
            val input = Parcel.obtain()
            val output = Parcel.obtain()
            try {
                input.writeInterfaceToken(LatencyProbeService.DESCRIPTOR)
                input.writeLong(call)
                input.writeString(request)
                input.writeString(dns)
                input.writeStrongBinder(callback)
                if (cancellation.get() != epoch) return null
                if (worker.transact(LatencyProbeService.MEASURE, input, output, 0)) output.readString() else null
            } finally {
                input.recycle()
                output.recycle()
            }
        } catch (_: Exception) {
            null
        } finally {
            watch.cancel()
            active.updateAndGet { if (it?.nonce == call) null else it }
            context.unbindService(connection)
            if (cancellation.get() != epoch) throw CancellationException("latency request cancelled")
        }
    }
}
