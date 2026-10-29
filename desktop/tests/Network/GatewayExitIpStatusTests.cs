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

        (await gateway.CheckExitAsync()).ShouldBe((CheckState.Warning, "same_ip"));
        var leaked = await changes.ReadAsync(TestContext.Current.CancellationToken);
        leaked.Protection.AllTraffic.ShouldBe(CheckState.Warning, "главный экран не должен оставаться зеленым при найденном обходе");
        leaked.HasExitIpLeak.ShouldBeTrue();
        leaked.ToContract().ToCore().HasExitIpLeak.ShouldBeTrue("тот же вывод доступен интерфейсу без новой версии контракта");
        gateway.AllTrafficReason.ShouldBe("same_ip");

        measured = null;
        (await gateway.CheckExitAsync()).Reason.ShouldBe("no_answer");
        gateway.Status.HasExitIpLeak.ShouldBeTrue("молчание проверочного сервиса не опровергает предыдущую найденную утечку");

        measured = "203.0.113.24";
        (await gateway.CheckExitAsync()).State.ShouldBe(CheckState.Ok);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Ok);
        gateway.Status.HasExitIpLeak.ShouldBeFalse();
        gateway.AllTrafficReason.ShouldBeNull();
        stop.Cancel();
    }

    [Fact]
    public async Task A_late_ip_measurement_cannot_mark_a_disconnected_session_as_leaking()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Connected((_, _) => answer.Task);
        var pending = gateway.CheckExitAsync();

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
