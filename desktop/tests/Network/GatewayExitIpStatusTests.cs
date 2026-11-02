using System.Reflection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Результат измерения идет в тот же снимок WatchStatus, который читает окно; сеть и TUN здесь — подделки.</summary>
public sealed class GatewayExitIpStatusTests
{
    [Fact]
    public async Task A_measured_real_ip_warns_watchers_and_only_a_verified_new_answer_clears_it()
    {
        string? measured = "198.51.100.7";
        await using var gateway = Connected((_, _) => Task.FromResult<string?>(measured));
        using var stop = new CancellationTokenSource();
        var (_, changes) = gateway.Watch(stop.Token);

        (await gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldBe((CheckState.Warning, "same_ip"));
        var leaked = await changes.ReadAsync(TestContext.Current.CancellationToken);
        leaked.Protection.AllTraffic.ShouldBe(CheckState.Warning, "главный экран не должен оставаться зеленым при найденном обходе");
        leaked.HasExitIpLeak.ShouldBeTrue();
        leaked.ToContract().ToCore().HasExitIpLeak.ShouldBeTrue("тот же вывод доступен интерфейсу без новой версии контракта");
        gateway.AllTrafficReason.ShouldBe("same_ip");
        gateway.Status.Protection.CheckedAt.ShouldNotBeNull();

        measured = null;
        (await gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken)).Reason.ShouldBe("no_answer");
        gateway.Status.HasExitIpLeak.ShouldBeTrue("молчание проверочного сервиса не опровергает предыдущую найденную утечку");
        gateway.Status.Protection.CheckedAt.ShouldBeNull("неудачная попытка не подтверждает проверку свежим временем");
        gateway.Status.ToContract().ToCore().Protection.CheckedAt.ShouldBeNull();

        measured = "203.0.113.24";
        (await gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken)).State.ShouldBe(CheckState.Ok);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Ok);
        gateway.Status.HasExitIpLeak.ShouldBeFalse();
        gateway.AllTrafficReason.ShouldBeNull();
        gateway.Status.Protection.CheckedAt.ShouldNotBeNull();
        stop.Cancel();
    }

    [Fact]
    public async Task Silence_invalidates_the_confirmation_time_without_inventing_a_leak_or_replacing_the_last_ip()
    {
        string? measured = "203.0.113.24";
        await using var gateway = Connected((_, _) => Task.FromResult<string?>(measured));

        (await gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken)).State.ShouldBe(CheckState.Ok);
        gateway.Status.Protection.CheckedAt.ShouldNotBeNull();

        measured = null;
        (await gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken)).Reason.ShouldBe("no_answer");

        gateway.Status.Protection.CheckedAt.ShouldBeNull();
        gateway.Status.Exit.Ip.ShouldBe("203.0.113.24", "последний измеренный адрес не подменяется при молчании");
        gateway.Status.HasExitIpLeak.ShouldBeFalse("отсутствие ответа не доказывает утечку");
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Ok, "действующая политика туннеля не отменяется предположением");
    }

    [Fact]
    public async Task A_cancelled_check_cancels_the_network_probe_and_does_not_publish_a_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Connected(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        using var stop = new CancellationTokenSource();
        var pending = gateway.CheckExitAsync(cancellationToken: stop.Token);
        await started.Task;

        stop.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        gateway.Status.Protection.CheckedAt.ShouldBeNull();
        gateway.Status.Exit.Ip.ShouldBeNull();
    }

    [Fact]
    public async Task A_late_ip_measurement_cannot_mark_a_disconnected_session_as_leaking()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Connected((_, _) => answer.Task);
        var pending = gateway.CheckExitAsync(cancellationToken: TestContext.Current.CancellationToken);

        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        answer.SetResult("198.51.100.7");
        await pending;

        gateway.Status.State.ShouldBe(ConnectionState.Off);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Off);
        gateway.Status.HasExitIpLeak.ShouldBeFalse();
    }

    private static Gateway Connected(Func<ServerProbe, CancellationToken, Task<string?>> measure)
    {
        var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"),
            Trace: new Uri("https://example.com/trace"), ExitIp: measure, BlockWatch: TimeSpan.FromHours(1)), TimeProvider.System,
            new ServiceJournal(TimeProvider.System));
        // Платформенный стенд задает уже подключенный снимок: реальный запуск TUN и изменение маршрутов
        // здесь не нужны. Все вызовы измерения, публикация WatchStatus и перевод IPC ниже — настоящие.
        var status = ConnectionStatus.Initial(DateTimeOffset.UtcNow)
            .MoveTo(ConnectionState.Connecting, DateTimeOffset.UtcNow).MoveTo(ConnectionState.Connected, DateTimeOffset.UtcNow);
        typeof(Gateway).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, status);
        typeof(Gateway).GetField("_directIp", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, "198.51.100.7");
        return gateway;
    }
}
