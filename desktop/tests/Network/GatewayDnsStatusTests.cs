using System.Net;
using System.Reflection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Отдельная DNS-проба не подменяет примененную политику. TUN, firewall и сеть здесь не меняются.</summary>
public sealed class GatewayDnsStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "dns_no_answer")]
    [InlineData(null, "dns_unavailable")]
    public async Task Unknown_probe_does_not_invent_a_leak_or_replace_applied_protection(bool? answer, string reason)
    {
        await using var gateway = Create((_, _) => Task.FromResult(answer));
        var initial = gateway.Status;
        using var stop = new CancellationTokenSource();
        var (_, changes) = gateway.Watch(stop.Token);

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, reason));

        gateway.Status.ShouldBe(initial, "неизвестный результат одной пробы не отменяет примененную политику");
        changes.TryRead(out _).ShouldBeFalse("не публикуется выдуманное изменение защиты");
        gateway.JournalLeaks().ShouldBeEmpty();
        stop.Cancel();
    }

    [Fact]
    public async Task A_missing_probe_is_unavailable_rather_than_a_successful_leak_check()
    {
        await using var gateway = Create(null, resolver: false);

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, "dns_unavailable"));
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Ok, "политика остается отдельным подтвержденным снимком");
    }

    [Fact]
    public async Task A_confirmed_answer_warns_the_status_and_unknown_does_not_clear_that_evidence()
    {
        bool? answer = true;
        await using var gateway = Create((_, _) => Task.FromResult(answer));
        using var stop = new CancellationTokenSource();
        var (_, changes) = gateway.Watch(stop.Token);

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, "dns_open"));
        (await changes.ReadAsync(Ct)).Protection.Dns.ShouldBe(CheckState.Warning);
        gateway.Status.ToContract().ToCore().Protection.Dns.ShouldBe(CheckState.Warning, "главная получает измеренное нарушение");

        answer = false;
        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, "dns_no_answer"));
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Warning, "молчание не опровергает прежний подтвержденный обход");
        answer = null;
        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, "dns_unavailable"));
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Warning);
        gateway.JournalLeaks().Count.ShouldBe(1, "неизвестные пробы не добавляют выдуманных нарушений");
        stop.Cancel();
    }

    [Theory]
    [InlineData(ConnectionMode.Proxy, ConnectionState.Connected, false)]
    [InlineData(ConnectionMode.Vpn, ConnectionState.Off, false)]
    [InlineData(ConnectionMode.Vpn, ConnectionState.Connecting, false)]
    [InlineData(ConnectionMode.Vpn, ConnectionState.Reconnecting, false)]
    [InlineData(ConnectionMode.Vpn, ConnectionState.Connected, true)]
    public async Task Inactive_or_proxy_modes_do_not_run_a_system_wide_vpn_probe(ConnectionMode mode, ConnectionState state, bool paused)
    {
        var calls = 0;
        await using var gateway = Create((_, _) =>
        {
            calls++;
            return Task.FromResult<bool?>(true);
        }, mode: mode, state: state, paused: paused);
        var before = gateway.Status;

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((before.Protection.Dns, (string?)null));

        calls.ShouldBe(0, "режим прокси не измеряет DNS всей системы, а неактивный VPN не начинает сетевую проверку");
        gateway.Status.ShouldBe(before);
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_does_not_publish_a_result()
    {
        var entered = Signal();
        await using var gateway = Create(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var before = gateway.Status;
        var pending = gateway.CheckDnsAsync(stop.Token);
        await entered.Task.WaitAsync(Ct);

        stop.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        gateway.Status.ShouldBe(before);
        gateway.JournalLeaks().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_callback_ignoring_cancellation_cannot_publish_its_late_answer()
    {
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Create((_, _) => answer.Task);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var before = gateway.Status;
        var pending = gateway.CheckDnsAsync(stop.Token);

        try
        {
            stop.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        }
        finally
        {
            answer.TrySetResult(true);
        }

        gateway.Status.ShouldBe(before);
        gateway.JournalLeaks().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_answer_cannot_publish_after_disconnect()
    {
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Create((_, _) => answer.Task);
        var pending = gateway.CheckDnsAsync(Ct);

        await gateway.DisconnectAsync(cancellationToken: Ct);
        answer.SetResult(true);

        (await pending).ShouldBe((CheckState.Off, (string?)null));
        gateway.Status.State.ShouldBe(ConnectionState.Off);
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Off);
        gateway.JournalLeaks().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("new_since")]
    [InlineData("network_time")]
    [InlineData("network_identity")]
    [InlineData("request")]
    [InlineData("offline")]
    public async Task An_old_probe_cannot_warn_a_newer_network_policy_or_pause(string change)
    {
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = Create((_, _) => answer.Task);
        var pending = gateway.CheckDnsAsync(Ct);
        var before = gateway.Status;
        var current = change switch
        {
            "paused" => before with { CaptivePauseUntil = before.Since.AddMinutes(5), Protection = ProtectionChecks.Paused },
            "new_since" => before with { Since = before.Since.AddTicks(1) },
            "network_time" => before with { NetworkChangedAt = before.Since.AddSeconds(1) },
            "network_identity" => before with { Network = new NetworkInfo(NetworkKind.WiFi, "test-network", false, false) },
            "offline" => before with { NetworkUnavailable = true },
            _ => before,
        };
        Set(gateway, "_status", current);
        if (change == "request")
        {
            Set(gateway, "_request", Request(ProtectionSettings.Default with { FakeDns = true }));
        }
        var expected = gateway.Status;
        answer.SetResult(true);

        (await pending).Reason.ShouldNotBe("dns_open");
        gateway.Status.ShouldBe(expected, "результат прежней карты или политики не меняет новый снимок");
        gateway.JournalLeaks().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_policy_exception_is_kept_without_a_probe_but_an_actual_probe_has_its_own_result(bool measure)
    {
        await using var gateway = Create((_, _) => Task.FromResult<bool?>(false), resolver: measure,
            protection: ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "192.168.1.53" });
        var status = gateway.Status;
        Set(gateway, "_status", status with { Protection = status.Protection with { Dns = CheckState.Warning } });

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, measure ? "dns_no_answer" : "own_dns"));
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Warning);
    }

    [Fact]
    public async Task A_failed_route_read_is_an_unavailable_probe_without_a_policy_change()
    {
        await using var gateway = Create(null, routes: new UnavailableRoutes());

        (await gateway.CheckDnsAsync(Ct)).ShouldBe((CheckState.Warning, "dns_unavailable"));
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Ok);
    }

    private static Gateway Create(Func<IPEndPoint, CancellationToken, Task<bool?>>? measure, bool resolver = true,
        ConnectionMode mode = ConnectionMode.Vpn, ConnectionState state = ConnectionState.Connected, bool paused = false,
        ProtectionSettings? protection = null, IRouteTable? routes = null)
    {
        var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"),
            OutsideDns: resolver ? new IPEndPoint(IPAddress.Loopback, 53) : null, DnsPastTunnel: measure,
            Routes: routes, BlockWatch: TimeSpan.FromHours(1)), TimeProvider.System, new ServiceJournal(TimeProvider.System));
        // Это модель уже примененного снимка: настоящие вызовы проверки, публикация и перевод IPC не требуют TUN или firewall.
        var now = DateTimeOffset.UtcNow;
        var status = ConnectionStatus.Initial(now) with
        {
            State = state,
            Mode = mode,
            ServerId = "dns-test",
            Protection = paused ? ProtectionChecks.Paused : ProtectionChecks.For(state, mode),
            CaptivePauseUntil = paused ? now.AddMinutes(5) : null,
        };
        Set(gateway, "_status", status);
        Set(gateway, "_request", Request(protection ?? ProtectionSettings.Default, mode));
        return gateway;
    }

    private static CoreRequest Request(ProtectionSettings protection, ConnectionMode mode = ConnectionMode.Vpn) => new(
        new ServerDefinition("dns-test", string.Empty, ProxyProtocol.Vless, "vpn.example.com", 8443, TransportOptions.Tcp,
            new SecurityOptions(TransportSecurity.Tls, "vpn.example.com"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: "11111111-2222-3333-4444-555555555555")),
        mode, protection, ConnectionSettings.Default, RoutingSettings.Default);

    private static void Set(Gateway gateway, string field, object value) =>
        typeof(Gateway).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, value);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class UnavailableRoutes : IRouteTable
    {
        public IReadOnlyList<DefaultRoute> DefaultRoutes() => throw new PlatformNotSupportedException("test route information unavailable");
    }
}

internal static class GatewayDnsTestJournal
{
    public static IReadOnlyList<JournalEntry> JournalLeaks(this Gateway gateway)
    {
        var journal = (ServiceJournal)typeof(Gateway).GetField("_journal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
        return journal.Tail(ServiceJournal.Capacity).Where(entry => entry.Message.StartsWith("Проверка утечек: запрос DNS", StringComparison.Ordinal)).ToArray();
    }
}
