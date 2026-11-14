using System.Net;
using System.Reflection;
using Grpc.Core;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящий IPC и подмененные измерения: отказ одной проверки не стирает остальные, TUN и firewall не меняются.</summary>
public sealed class DiagnosticIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Uri Trace = new("https://example.com/trace");
    private static readonly Uri Trace6 = new("https://example.com/trace6");
    private const string Exit = "203.0.113.24";
    private const string FakeSecret = "not-a-real-password-diagnostics";

    [Theory]
    [InlineData("exit_ip")]
    [InlineData("ipv6")]
    [InlineData("dns")]
    [InlineData("other_vpn")]
    public async Task One_failed_probe_preserves_local_and_other_results_over_ipc(string failed)
    {
        await using var service = await Start(options => options with
        {
            ExitIp = (probe, _) => probe.Target == Trace && failed == "exit_ip" || probe.Target == Trace6 && failed == "ipv6"
                ? throw Failure() : Task.FromResult<string?>(probe.Target == Trace ? Exit : null),
            DnsPastTunnel = (_, _) => failed == "dns" ? throw Failure() : Task.FromResult<bool?>(false),
            Routes = new Routes(failed == "other_vpn" ? Failure() : null),
        });

        var report = await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct);

        report.Results.Count.ShouldBe(OperatingSystem.IsWindows() ? 9 : 8);
        Row(report, "service").State.ShouldBe(V1.CheckState.Ok);
        Row(report, "core").State.ShouldBe(V1.CheckState.Ok);
        Row(report, "all_traffic").Reason.ShouldBeEmpty();
        Row(report, "kill_switch").Reason.ShouldBeEmpty();
        Row(report, failed).State.ShouldBe(V1.CheckState.Warning);
        Row(report, failed).Reason.ShouldBe("probe_failed");
        Row(report, "exit_ip").Reason.ShouldBe(failed == "exit_ip" ? "probe_failed" : string.Empty);
        Row(report, "ipv6").Reason.ShouldBe(failed == "ipv6" ? "probe_failed" : string.Empty);
        Row(report, "dns").Reason.ShouldBe(failed == "dns" ? "probe_failed" : "dns_no_answer");
        Row(report, "other_vpn").Reason.ShouldBe(failed == "other_vpn" ? "probe_failed" : string.Empty);
        var error = service.Journal.Tail(50).Single(e => e.Message.StartsWith("Проверка " + failed + " не выполнена:", StringComparison.Ordinal));
        error.Message.ShouldContain("System.InvalidOperationException; HRESULT=0x80131509");
        error.Message.ShouldContain("RPC=Internal");
        AssertNoSecrets(service.Journal);
    }

    [Fact]
    public async Task Internal_probe_timeout_does_not_cancel_unrelated_results()
    {
        await using var service = await Start(options => options with
        {
            ExitIp = (probe, _) => probe.Target == Trace6 ? throw new TimeoutException(FakeSecret) : Task.FromResult<string?>(Exit),
        });

        var report = await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct);

        Row(report, "ipv6").Reason.ShouldBe("timeout");
        Row(report, "exit_ip").State.ShouldBe(V1.CheckState.Ok);
        Row(report, "service").State.ShouldBe(V1.CheckState.Ok);
        AssertNoSecrets(service.Journal);
    }

    [Fact]
    public async Task Cancelling_ipc_does_not_return_success_when_a_probe_ignores_cancellation()
    {
        var entered = Signal();
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = Signal();
        await using var service = await Start(options => options with
        {
            ExitIp = (probe, token) =>
            {
                if (probe.Target == Trace)
                {
                    return Task.FromResult<string?>(Exit);
                }

                token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                return answer.Task;
            },
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: stop.Token);
        await entered.Task.WaitAsync(Ct);

        stop.Cancel();
        var error = await Should.ThrowAsync<RpcException>(() => call.ResponseAsync);
        error.StatusCode.ShouldBe(StatusCode.Cancelled);
        await cancelled.Task.WaitAsync(Ct);
        answer.SetResult("2001:db8::24");

        service.Gateway.Status.Protection.Ipv6.ShouldBe(CheckState.Ok);
        service.Journal.Tail(50).ShouldNotContain(e => e.Message.StartsWith("Проверка ipv6 не выполнена:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_ipv6_probe_cannot_publish_its_late_address()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = await Start(options => options with { ExitIp = (_, _) => answer.Task });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = service.Gateway.CheckIpv6Async(stop.Token);
        stop.Cancel();
        answer.SetResult("2001:db8::24");

        await Should.ThrowAsync<OperationCanceledException>(() => pending);

        service.Gateway.Status.Protection.Ipv6.ShouldBe(CheckState.Ok);
        service.Journal.Tail(50).ShouldNotContain(e => e.Level == JournalLevel.Error);
    }

    [Fact]
    public async Task Automatic_leak_checks_keep_running_after_an_ipv6_failure()
    {
        var dnsCalls = 0;
        await using var service = await Start(options => options with
        {
            ExitIp = (probe, _) => probe.Target == Trace6 ? throw Failure() : Task.FromResult<string?>(Exit),
            DnsPastTunnel = (_, _) =>
            {
                dnsCalls++;
                return Task.FromResult<bool?>(false);
            },
        });

        var pending = (Task)typeof(Gateway).GetMethod("LeakCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service.Gateway, null)!;
        await pending.WaitAsync(Ct);

        dnsCalls.ShouldBe(1, "ошибка IPv6 не отменяет следующую DNS-пробу");
        service.Journal.Tail(50).ShouldContain(e => e.Message.StartsWith("Проверка after_connect.ipv6 не выполнена:", StringComparison.Ordinal));
        AssertNoSecrets(service.Journal);
    }

    [Fact]
    public async Task A_network_change_discards_old_probe_results_and_cannot_publish_an_ipv6_leak()
    {
        var entered = Signal();
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = await Start(options => options with
        {
            ExitIp = (probe, _) =>
            {
                if (probe.Target == Trace)
                {
                    return Task.FromResult<string?>(Exit);
                }

                entered.TrySetResult();
                return answer.Task;
            },
        });
        var call = service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct);
        await entered.Task.WaitAsync(Ct);
        Set(service.Gateway, "_status", service.Gateway.Status with { NetworkChangedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        answer.SetResult("2001:db8::24");

        var report = await call;

        foreach (var id in new[] { "exit_ip", "ipv6", "dns", "other_vpn" })
        {
            Row(report, id).State.ShouldBe(V1.CheckState.Warning);
            Row(report, id).Reason.ShouldBe("session_changed");
        }
        Row(report, "service").State.ShouldBe(V1.CheckState.Ok);
        service.Gateway.Status.Protection.Ipv6.ShouldBe(CheckState.Ok, "поздний адрес другой сети не доказывает утечку текущей");
        service.Journal.Tail(50).ShouldNotContain(e => e.Level == JournalLevel.Error);
    }

    [Fact]
    public async Task A_late_ipv4_result_cannot_publish_a_leak_after_only_the_network_changed()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = await Start(options => options with { ExitIp = (_, _) => answer.Task });
        var pending = service.Gateway.CheckExitAsync(cancellationToken: Ct);
        Set(service.Gateway, "_status", service.Gateway.Status with { NetworkChangedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        answer.SetResult("198.51.100.7");

        (await pending).ShouldBe((CheckState.Warning, "session_changed"));

        service.Gateway.Status.HasExitIpLeak.ShouldBeFalse();
        service.Gateway.Status.Exit.Ip.ShouldBe(Exit);
        service.Journal.Tail(50).ShouldNotContain(e => e.Level == JournalLevel.Error);
    }

    [Theory]
    [InlineData("ipv6")]
    [InlineData("webrtc")]
    [InlineData("local")]
    public async Task Comparison_preserves_available_fields_when_one_measurement_fails(string failed)
    {
        var mapped = new IPEndPoint(IPAddress.Parse("203.0.113.25"), 12345);
        await using var service = await Start(options => options with
        {
            ExitIp = (_, _) => failed == "ipv6" ? throw Failure() : Task.FromResult<string?>("2001:db8::24"),
            Webrtc = _ => failed == "webrtc" ? throw Failure() : Task.FromResult<IPEndPoint?>(mapped),
            NetworkDetails = new Details(failed == "local" ? Failure() : null),
        });

        var result = await service.Client.CompareNetworkAsync(new V1.CompareNetworkRequest(), cancellationToken: Ct);

        result.Now.ShouldNotBeNull();
        result.Now.Ipv4.ShouldBe(Exit);
        result.Now.Ipv6.ShouldBe(failed == "ipv6" ? string.Empty : "2001:db8::24");
        result.Now.WebrtcAddress.ShouldBe(failed == "webrtc" ? string.Empty : mapped.Address.ToString());
        if (failed == "local")
        {
            result.Now.Route.ShouldBeEmpty();
            result.Now.Dns.ShouldBeEmpty();
        }
        service.Journal.Tail(50).ShouldContain(e => e.Message.StartsWith("Проверка comparison." + failed + " не выполнена:", StringComparison.Ordinal));
        AssertNoSecrets(service.Journal);
    }

    [Fact]
    public async Task Cancelled_comparison_propagates_cancellation_instead_of_returning_partial_success()
    {
        var entered = Signal();
        await using var service = await Start(options => options with
        {
            ExitIp = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = service.Gateway.CompareNetworkAsync(stop.Token);
        await entered.Task.WaitAsync(Ct);

        stop.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        service.Journal.Tail(50).ShouldNotContain(e => e.Message.StartsWith("Проверка comparison.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Comparison_does_not_mix_old_addresses_with_a_new_session()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = await Start(options => options with { ExitIp = (_, _) => answer.Task });
        var pending = service.Gateway.CompareNetworkAsync(Ct);
        Set(service.Gateway, "_status", service.Gateway.Status with { NetworkChangedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        answer.SetResult("2001:db8::24");

        var comparison = await pending;

        comparison.Before.ShouldBeNull();
        comparison.Now.Ipv4.ShouldBeNull();
        comparison.Now.Ipv6.ShouldBeNull();
        comparison.Now.Webrtc.ShouldBeNull();
        comparison.Now.Route.ShouldBeNull();
        comparison.Now.Dns.ShouldBeEmpty();
    }

    private static async Task<RunningService> Start(Func<GatewayOptions, GatewayOptions> configure)
    {
        var service = await RunningService.StartAsync(options: options => configure(options with
        {
            XrayPath = null,
            Trace = Trace,
            Trace6 = Trace6,
            ExitIp = (probe, _) => Task.FromResult<string?>(probe.Target == Trace ? Exit : null),
            OutsideDns = new IPEndPoint(IPAddress.Loopback, 53),
            DnsPastTunnel = (_, _) => Task.FromResult<bool?>(false),
            Routes = new Routes(null),
            NetworkDetails = new Details(null),
            BlockWatch = TimeSpan.FromHours(1),
            Webrtc = _ => Task.FromResult<IPEndPoint?>(null),
        }));
        var now = DateTimeOffset.UtcNow;
        Set(service.Gateway, "_status", ConnectionStatus.Initial(now) with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Vpn,
            ServerId = "diagnostics-test",
            Protection = ProtectionChecks.For(ConnectionState.Connected),
            Exit = ExitInfo.Unknown with { Ip = Exit, DirectIp = "198.51.100.7" },
        });
        Set(service.Gateway, "_directIp", "198.51.100.7");
        return service;
    }

    private static InvalidOperationException Failure()
    {
        var error = new InvalidOperationException(FakeSecret + " https://example.com/private-subscription /Users/example/private.txt",
            new RpcException(new Status(StatusCode.Internal, FakeSecret)));
        error.Data["secret"] = FakeSecret;
        return error;
    }

    private static void AssertNoSecrets(ServiceJournal journal)
    {
        var text = string.Join('\n', journal.Tail(50).Select(e => e.Message));
        text.ShouldNotContain(FakeSecret);
        text.ShouldNotContain("private-subscription");
        text.ShouldNotContain("/Users/example");
    }

    private static V1.CheckResult Row(V1.CheckReport report, string id) => report.Results.Single(r => r.Id == id);

    private static void Set(Gateway gateway, string field, object value) =>
        typeof(Gateway).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, value);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Routes(Exception? failure) : IRouteTable
    {
        public IReadOnlyList<DefaultRoute> DefaultRoutes() => failure is not null ? throw failure : [];
    }

    private sealed class Details(Exception? failure) : INetworkDetails
    {
        public IReadOnlyList<NetworkRoute> Routes() => failure is not null ? throw failure : [];
        public DhcpLease? Dhcp(string adapter) => null;
        public IReadOnlyList<SocketEntry> Sockets() => [];
    }
}
