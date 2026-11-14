using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class ReconnectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_restart_uses_the_connect_timeout_and_accepts_its_second_probe_without_another_restart(bool firstProbeThrows)
    {
        var recovering = false;
        var calls = 0;
        var observed = new ConcurrentQueue<TimeSpan>();
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            if (!Volatile.Read(ref recovering))
            {
                return Task.FromResult(true);
            }

            observed.Enqueue(probe.Timeout);
            if (Interlocked.Increment(ref calls) == 1)
            {
                return firstProbeThrows ? Task.FromException<bool>(new IOException("private test marker")) : Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        var timing = Fast with { Every = TimeSpan.FromHours(1), ProbeTimeout = TimeSpan.FromMilliseconds(25) };
        await using var rig = await ProxyRig.CreateAsync(timing, probe: Probe);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        request.Connection.ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(37));
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var original = CoreProcessId(rig.Service);

        Volatile.Write(ref recovering, true);
        using (var process = Process.GetProcessById(original))
        {
            process.Kill();
        }
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);

        observed.ShouldBe([TimeSpan.FromSeconds(37), TimeSpan.FromSeconds(37)]);
        Attempts(rig.Service).ShouldBe(1, "первая неудачная проба не требует еще одного перезапуска ядра");
        CoreProcessId(rig.Service).ShouldNotBe(original);
        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Message.Contains("private test marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_finite_policy_bounds_a_restart_probe_and_does_not_accept_a_late_success()
    {
        var recovering = false;
        var calls = 0;
        var observed = TimeSpan.Zero;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            if (!Volatile.Read(ref recovering))
            {
                return Task.FromResult(true);
            }
            observed = probe.Timeout;
            Interlocked.Increment(ref calls);
            return answer.Task;
        }

        var time = new FakeTimeProvider();
        var timing = Fast with { Every = TimeSpan.FromHours(1), GiveUpAfter = TimeSpan.FromSeconds(30) };
        await using var rig = await ProxyRig.CreateAsync(timing, probe: Probe, time: time);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        request.Connection.ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(37));
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Connected, time);
        Volatile.Write(ref recovering, true);
        using (var process = Process.GetProcessById(CoreProcessId(rig.Service)))
        {
            process.Kill();
        }

        try
        {
            await WaitOfflineStateAsync(rig.Service.Gateway, _ => Volatile.Read(ref calls) > 0, time);
            observed.ShouldBeGreaterThan(TimeSpan.Zero);
            observed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30), "проба не получает больше остатка политики");
            time.Advance(TimeSpan.FromSeconds(31));
            answer.TrySetResult(true);
            await WaitOfflineStateAsync(rig.Service.Gateway, s => s.State == ConnectionState.Error, time);

            calls.ShouldBe(1, "истекшая политика не начинает вторую пробу и не подтверждает запоздалый ответ");
            rig.Service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.ServerNotResponding);
            Attempts(rig.Service).ShouldBe(1);
        }
        finally
        {
            answer.TrySetResult(false);
        }
    }

    [Fact]
    public async Task Disconnect_during_the_first_restart_probe_does_not_probe_or_restore_the_old_session_again()
    {
        var recovering = false;
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Probe(ServerProbe probe, CancellationToken cancellationToken)
        {
            if (!Volatile.Read(ref recovering))
            {
                return Task.FromResult(true);
            }
            Interlocked.Increment(ref calls);
            cancellationToken.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return answer.Task;
        }

        await using var rig = await ProxyRig.CreateAsync(Fast with { Every = TimeSpan.FromHours(1) }, probe: Probe);
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        Volatile.Write(ref recovering, true);
        using (var process = Process.GetProcessById(CoreProcessId(rig.Service)))
        {
            process.Kill();
        }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await rig.Service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
            answer.TrySetResult(true);
            await Task.Delay(200, Ct);

            calls.ShouldBe(1, "отмененное подтверждение не начинает вторую пробу с токеном нового запуска");
            rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Off);
            Attempts(rig.Service).ShouldBe(1);
        }
        finally
        {
            answer.TrySetResult(false);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_core_stop_keeps_reconnection_alive_or_reports_the_exhausted_policy(bool stopRecovers)
    {
        var available = true;
        var network = new FakeNetwork();
        var hour = TimeSpan.FromHours(1);
        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, TimeSpan.FromSeconds(3)),
            network, probe: (_, _) => Task.FromResult(Volatile.Read(ref available)));
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort());
        request.Connection.Reconnect = stopRecovers ? V1.ReconnectPolicy.Backoff60S : V1.ReconnectPolicy.UpTo5Minutes;
        await rig.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        Volatile.Write(ref available, false);
        network.Raise();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting);
        Volatile.Write(ref available, true);

        // Отказ системного объекта процесса при остановке, без убийства чужих процессов или изменения ОС.
        var core = (CoreProcess)typeof(Gateway).GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Service.Gateway)!;
        var processField = typeof(CoreProcess).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Process)processField.GetValue(core)!;
        var originalId = original.Id;
        using var unavailable = new Process();
        processField.SetValue(core, unavailable);
        try
        {
            network.Raise();
            await WaitOfflineStateAsync(rig.Service.Gateway, _ => rig.Service.Journal.Tail(ServiceJournal.Capacity)
                .Any(e => e.Message.Contains("операция не завершена (InvalidOperationException); будет повтор", StringComparison.Ordinal)));

            rig.Service.Gateway.Status.State.ShouldBe(ConnectionState.Reconnecting);
            original.HasExited.ShouldBeFalse("отказ не должен потерять еще работающее ядро");
            typeof(Gateway).GetField("_core", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Service.Gateway).ShouldBeSameAs(core);

            if (stopRecovers)
            {
                processField.SetValue(core, original);
                network.Raise();
                await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, rig.Service.Journal);
                CoreProcessId(rig.Service).ShouldNotBe(originalId);
            }
            else
            {
                var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error, rig.Service.Journal);
                states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected);
                states[^1].Error.Code.ShouldBe(ErrorCodes.CoreFailed);
                rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
                    e.Message.Contains("Фоновое восстановление остановлено", StringComparison.Ordinal));
            }
        }
        finally
        {
            processField.SetValue(core, original);
        }
    }
}
