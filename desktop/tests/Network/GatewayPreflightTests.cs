using System.Net;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Свежая проверка профиля не меняет сеть при отказе, отмене и замене принятого намерения.</summary>
public sealed class GatewayPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-preflight-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Failed_profile_is_rejected_before_firewall_or_tunnel_and_the_internet_stays_open()
    {
        var firewall = new FakeFirewall();
        var checkedProfiles = new List<CoreRequest>();
        await using var gateway = Create(firewall, (core, _, _) =>
        {
            checkedProfiles.Add(core);
            return Task.FromResult(ProbeReachability.TimedOut());
        });

        var reply = await gateway.ConnectAsync(Request("198.51.100.20", "unavailable"), TestContext.Current.CancellationToken);

        reply.Accepted.ShouldBeFalse();
        reply.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        checkedProfiles.Count.ShouldBe(1, "проверяется выбранный профиль, а не старое значение задержки");
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Off);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Off);
        firewall.Enables.ShouldBe(0, "до успешной проверки новое закрытие интернета не устанавливается");
        firewall.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Refused_profile_on_a_live_port_names_the_reason_of_the_core()
    {
        // Отчет пользователя 2026-11-06 18:46: «Сервер отвечает по TCP, но связи через него нет», а почему — строка Xray среди
        // обычных записей, ниже ее заняли строки DNS. Теперь причина ядра — в самом выводе.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var journal = new ServiceJournal(TimeProvider.System);
        await using var gateway = Create(new FakeFirewall(), (_, _, _) =>
        {
            journal.Add(JournalLevel.Info, "xray", "2026/11/06 18:46:01.000000 [Warning] [12] app/proxyman/outbound: failed to process "
                + "outbound traffic > proxy/vless/outbound: failed to find an available destination");
            journal.Add(JournalLevel.Info, "xray", "2026/11/06 18:46:13.000000 [Error] app/dns: failed to retrieve response for ***.");
            return Task.FromResult(ProbeReachability.TimedOut());
        }, journal: journal);

        var reply = await gateway.ConnectAsync(Request("127.0.0.1", "refused", port), TestContext.Current.CancellationToken);

        reply.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!journal.Tail(ServiceJournal.Capacity).Any(e => e.Message.StartsWith("Сервер отвечает по TCP", StringComparison.Ordinal))
            && waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        journal.Tail(ServiceJournal.Capacity).Single(e => e.Message.StartsWith("Сервер отвечает по TCP", StringComparison.Ordinal)).Message
            .ShouldEndWith(". Ядро: [Warning] [12] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: "
                + "failed to find an available destination");
    }

    [Fact]
    public async Task Disconnect_cancels_the_preflight_and_a_late_success_cannot_start_a_tunnel()
    {
        var entered = Signal();
        var answer = new TaskCompletionSource<ProbeReachability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firewall = new FakeFirewall();
        await using var gateway = Create(firewall, (_, _, _) =>
        {
            entered.TrySetResult();
            return answer.Task; // Имитация долгого ответа, который сам не замечает отмену.
        });

        var pending = gateway.ConnectAsync(Request("198.51.100.20", "old"), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Connecting);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Off, "во время предварительной проверки трафик еще прямой");
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Off);

        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Accepted.ShouldBeFalse();
        answer.TrySetResult(ProbeReachability.Responded(HttpStatusCode.OK));

        gateway.Status.State.ShouldBe(ConnectionState.Off);
        firewall.Enables.ShouldBe(0);
    }

    [Fact]
    public async Task An_old_preflight_never_replaces_the_result_of_a_newer_server()
    {
        var entered = Signal();
        var answer = new TaskCompletionSource<ProbeReachability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firewall = new FakeFirewall();
        await using var gateway = Create(firewall, (core, _, _) =>
        {
            if (core.Server.Host == "198.51.100.20")
            {
                entered.TrySetResult();
                return answer.Task;
            }

            return Task.FromResult(ProbeReachability.TimedOut());
        });
        var old = gateway.ConnectAsync(Request("198.51.100.20", "old"), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var latest = await gateway.ConnectAsync(Request("198.51.100.30", "new"), TestContext.Current.CancellationToken);
        var status = gateway.Status;
        answer.TrySetResult(ProbeReachability.Responded(HttpStatusCode.OK));

        latest.Accepted.ShouldBeFalse();
        (await old.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Accepted.ShouldBeFalse();
        gateway.Status.ShouldBe(status);
        gateway.Status.ServerId.ShouldBe("new");
        firewall.Enables.ShouldBe(0);
    }

    [Fact]
    public async Task A_non_cooperating_probe_is_still_bounded_by_the_connection_timeout()
    {
        var clock = new FakeTimeProvider();
        var entered = Signal();
        var answer = new TaskCompletionSource<ProbeReachability>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firewall = new FakeFirewall();
        await using var gateway = Create(firewall, (_, _, _) =>
        {
            entered.TrySetResult();
            return answer.Task;
        }, clock);
        var request = Request("198.51.100.20", "slow");
        request.Connection.ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(5));
        var pending = gateway.ConnectAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(5));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.Accepted.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        firewall.Enables.ShouldBe(0);
        answer.TrySetResult(ProbeReachability.Responded(HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_probe_startup_failure_finishes_with_a_safe_error_instead_of_leaving_connecting()
    {
        var firewall = new FakeFirewall();
        var journal = new ServiceJournal(TimeProvider.System);
        await using var gateway = Create(firewall, (_, _, _) =>
            throw new IOException("fake sensitive profile path https://example.com/never-log-this"), journal: journal);

        var reply = await gateway.ConnectAsync(Request("198.51.100.20", "core-failure"), TestContext.Current.CancellationToken);

        reply.Accepted.ShouldBeFalse();
        reply.Error.Code.ShouldBe(ErrorCodes.CoreFailed);
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        firewall.Enables.ShouldBe(0);
        journal.Tail(ServiceJournal.Capacity).ShouldNotContain(entry => entry.Message.Contains("never-log-this", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_attempt_rechecks_a_previously_unavailable_profile_and_does_not_keep_an_old_negative_result()
    {
        var firewall = new FakeFirewall { FailOnEnable = true };
        var attempts = 0;
        await using var gateway = Create(firewall, (_, _, _) => Task.FromResult(++attempts == 1
            ? ProbeReachability.TimedOut()
            : ProbeReachability.Responded(HttpStatusCode.OK)));

        (await gateway.ConnectAsync(Request("198.51.100.20", "same"), TestContext.Current.CancellationToken)).Accepted.ShouldBeFalse();
        firewall.Enables.ShouldBe(0);
        await gateway.ConnectAsync(Request("198.51.100.20", "same"), TestContext.Current.CancellationToken);

        attempts.ShouldBe(2, "прежнее «нет» не заменяет свежую проверку");
        firewall.Enables.ShouldBe(1, "на второй попытке доступный профиль проходит к стадии включения защиты");
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.KillSwitchFailed, "заведомый отказ fake firewall останавливает тест до настоящего TUN");
    }

    [Fact]
    public async Task A_hostname_profile_is_checked_again_after_disconnect_even_with_the_previous_request_saved()
    {
        var firewall = new FakeFirewall { FailOnEnable = true };
        var attempts = 0;
        await using var gateway = Create(firewall, (_, _, _) => Task.FromResult(++attempts == 1
            ? ProbeReachability.Responded(HttpStatusCode.OK)
            : ProbeReachability.TimedOut()), resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.51.100.20") }));

        await gateway.ConnectAsync(Request("vpn.example.com", "same-hostname"), TestContext.Current.CancellationToken);
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        firewall.FailOnEnable = false;
        var before = firewall.Enables;
        var reply = await gateway.ConnectAsync(Request("vpn.example.com", "same-hostname"), TestContext.Current.CancellationToken);

        attempts.ShouldBe(2, "запомненный _request после отключения не означает, что старый запуск еще идет");
        reply.Accepted.ShouldBeFalse();
        reply.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        firewall.Enables.ShouldBe(before, "на повторной неудачной проверке правило блока не устанавливается");
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Off);
    }

    [Fact]
    public async Task The_address_that_answered_is_promoted_for_the_actual_vpn_start()
    {
        var good = IPAddress.Parse("198.51.100.21");
        var firewall = new FakeFirewall { FailOnEnable = true };
        var tried = new List<IPAddress>();
        await using var gateway = Create(firewall, (_, address, _) =>
        {
            tried.Add(address);
            return Task.FromResult(address.Equals(good) ? ProbeReachability.Responded(HttpStatusCode.OK) : ProbeReachability.TimedOut());
        }, resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.51.100.20"), good }));

        await gateway.ConnectAsync(Request("vpn.example.com", "several-addresses"), TestContext.Current.CancellationToken);

        tried.ShouldBe([IPAddress.Parse("198.51.100.20"), good]);
        firewall.LastPolicy.ShouldNotBeNull().Servers[0].Address.ShouldBe(good,
            "обычный запуск берет адрес с индексом 0, поэтому результат пробы должен дойти до его политик и TUN");
    }

    [Fact]
    public async Task An_existing_boot_block_is_not_opened_for_preflight()
    {
        var path = Path.Combine(_root, "boot.json");
        BootBlock.Save(path, new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: true));
        var firewall = new FakeFirewall { Enabled = true };
        var checks = 0;
        await using var gateway = Create(firewall, (_, _, _) =>
        {
            checks++;
            return Task.FromResult(ProbeReachability.Responded(HttpStatusCode.OK));
        }, boot: path);
        firewall.FailOnEnable = true;

        await gateway.ConnectAsync(Request("198.51.100.20", "blocked"), TestContext.Current.CancellationToken);

        checks.ShouldBe(0, "под блоком действует ограниченная попытка с прежними гарантиями, а не прямой обход проверки");
        firewall.Disables.ShouldBe(0);
        firewall.Enabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_real_profile_probe_tries_another_resolved_address_and_preserves_its_tls_identity(bool explicitSni)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless-ws-tls", port);
        pair.Config["inbounds"]![0]!["streamSettings"]!["tlsSettings"]!["rejectUnknownSni"] = true;
        using var server = await XrayKit.Start(pair.Config, port);
        using var site = new LocalSite("preflight through the profile");
        var definition = pair.Server with
        {
            Host = explicitSni ? "vpn.example.com" : "example.com",
            Security = pair.Server.Security with { Sni = explicitSni ? "example.com" : null },
        };
        var firewall = new FakeFirewall { FailOnEnable = true };
        await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, site.Url, SingBoxPath: XrayKit.SingBox,
            KillSwitch: firewall, Reconnect: ReconnectTiming.Default,
            Resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback })),
            TimeProvider.System, new ServiceJournal(TimeProvider.System));
        var request = Request(definition.Host, "real-tls-profile");
        request.Server = definition.ToContract("real-tls-profile");

        await gateway.ConnectAsync(request, TestContext.Current.CancellationToken);

        site.Requests.ShouldBe(1);
        firewall.Enables.ShouldBe(1, "производственная настройка периодических проб включает и свежую проверку до firewall");
        firewall.LastPolicy.ShouldNotBeNull().Servers[0].Address.ShouldBe(IPAddress.Loopback);
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.KillSwitchFailed, "fake firewall останавливает интеграционную проверку до настоящего TUN");
        definition.Host.ShouldBe(explicitSni ? "vpn.example.com" : "example.com", "проверка закрепленного IP не переписывает исходный профиль");
        definition.Security.Sni.ShouldBe(explicitSni ? "example.com" : null);
    }

    [Fact]
    public async Task A_real_profile_with_wrong_credentials_cannot_pass_preflight_by_reaching_the_site_directly()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless-ws-tls", port);
        using var server = await XrayKit.Start(pair.Config, port);
        using var site = new LocalSite("must not bypass the selected profile");
        var invalid = pair.Server with
        {
            Credentials = pair.Server.Credentials with { Uuid = "22222222-3333-4444-5555-666666666666" },
        };
        var firewall = new FakeFirewall { FailOnEnable = true };
        await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, site.Url, SingBoxPath: XrayKit.SingBox,
            KillSwitch: firewall, Reconnect: ReconnectTiming.Default), TimeProvider.System, new ServiceJournal(TimeProvider.System));
        var request = Request(invalid.Host, "bad-credentials");
        request.Server = invalid.ToContract("bad-credentials");
        request.Connection.ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(5));

        var reply = await gateway.ConnectAsync(request, TestContext.Current.CancellationToken);

        reply.Accepted.ShouldBeFalse();
        reply.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        firewall.Enables.ShouldBe(0);
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Off);
        site.Requests.ShouldBe(0, "ответ обычной сети не должен подтверждать неработающие данные доступа VPN");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_forced_binding_uses_only_a_verified_unbound_path_and_preserves_authentication(bool validCredentials)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        // Запуск удерживает проверка IPv6 сервера; без IPv6 в ядре ОС (контейнер облачной копии) ее нет, держать нечем.
        Assert.SkipUnless(VpnTunnel.AdapterIpv6(), "нет IPv6 в ядре ОС — проверки IPv6 сервера перед TUN нет");
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless-ws-tls", port);
        using var server = await XrayKit.Start(pair.Config, port);
        using var site = new LocalSite("verified path without interface binding");
        var definition = validCredentials ? pair.Server : pair.Server with
        {
            Credentials = pair.Server.Credentials with { Uuid = "22222222-3333-4444-5555-666666666666" },
        };
        var firewall = new FakeFirewall();
        var startedVpnCore = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdBeforeTun = Signal();
        async Task<string?> HoldBeforeTun(ServerProbe probe, CancellationToken cancellationToken)
        {
            var reached = await LatencyProbe.ReachesAsync(probe with { Timeout = TimeSpan.FromSeconds(2) }, cancellationToken);
            startedVpnCore.TrySetResult(reached);
            await holdBeforeTun.Task.WaitAsync(cancellationToken);
            return null;
        }

        await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, site.Url, SingBoxPath: XrayKit.SingBox,
            KillSwitch: firewall, Reconnect: ReconnectTiming.Default, Routes: new MissingInterfaceRoutes(),
            Trace: site.Url, Trace6: site.Url, ExitIp: HoldBeforeTun), TimeProvider.System, new ServiceJournal(TimeProvider.System));
        var request = Request(definition.Host, "binding-fallback");
        request.Server = definition.ToContract("binding-fallback");
        try
        {
            var reply = await gateway.ConnectAsync(request, TestContext.Current.CancellationToken);
            if (validCredentials)
            {
                reply.Accepted.ShouldBeTrue("сбой привязки не делает тот же отвечающий профиль недействительным");
                (await startedVpnCore.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                    .ShouldBeTrue("реальное ядро VPN должно сохранить проверенный путь без повторного включения неисправной привязки");
                firewall.Enables.ShouldBe(1);
                site.Requests.ShouldBeGreaterThanOrEqualTo(2, "ответ получен и предварительной проверкой, и настоящим ядром до TUN");
            }
            else
            {
                reply.Accepted.ShouldBeFalse();
                reply.Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
                startedVpnCore.Task.IsCompleted.ShouldBeFalse();
                firewall.Enables.ShouldBe(0);
                site.Requests.ShouldBe(0, "запасной путь сохраняет проверку доступа и не идет напрямую к проверочному сайту");
            }
        }
        finally
        {
            // Callback держит настоящее ядро перед sing-box: отключение отменяет его и не создает TUN в ОС.
            await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_working_forced_binding_keeps_the_fast_path_without_a_second_profile_probe()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless-ws-tls", port);
        using var server = await XrayKit.Start(pair.Config, port);
        using var site = new LocalSite("working bound profile");
        var outside = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .First(adapter => adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Name;
        var core = new CoreRequest(pair.Server, ConnectionMode.Vpn, ProtectionSettings.Default,
            ConnectionSettings.Default, RoutingSettings.Default);

        var result = await ConnectionPreflight.CheckAsync(XrayKit.Xray!, XrayKit.SingBox, core,
            [IPAddress.Loopback], site.Url, TimeSpan.FromSeconds(10), TimeProvider.System, outside,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Reachability.Reached.ShouldBeTrue(result.Reachability.Description);
        result.UseOutsideBinding.ShouldBeTrue("проверенная рабочая привязка не заменяется запасным путем");
        site.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Many_dns_addresses_do_not_starve_a_healthy_profile_of_startup_and_handshake_time()
    {
        var clock = new FakeTimeProvider();
        var entered = Signal();
        var addresses = Enumerable.Range(1, KillSwitchPolicy.MaxServers)
            .Select(i => IPAddress.Parse("198.51.100." + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var core = new CoreRequest(XrayKit.Definition(ProxyProtocol.Vless, 443, new ServerCredentials(Uuid: XrayKit.Uuid)),
            ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
        var checkedAddresses = new List<IPAddress>();
        async Task<ProbeReachability> Check(CoreRequest _, IPAddress address, CancellationToken cancellationToken)
        {
            checkedAddresses.Add(address);
            if (address.Equals(addresses[0]))
            {
                entered.TrySetResult();
                // Моделирует конечное время запуска настоящего ядра и двух сетевых рукопожатий.
                await Task.Delay(TimeSpan.FromSeconds(2), clock, cancellationToken);
                return ProbeReachability.Responded(HttpStatusCode.OK);
            }

            return ProbeReachability.TimedOut();
        }

        var pending = ConnectionPreflight.CheckAsync("unused fake core", null, core, addresses,
            new Uri("https://example.com/"), TimeSpan.FromSeconds(10), clock, probe: Check,
            cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.Reachability.Reached.ShouldBeTrue();
        result.Address.ShouldBe(addresses[0]);
        checkedAddresses.ShouldBe([addresses[0]], "здоровый профиль нельзя отвергать после 0,625 с только из-за 16 адресов DNS");
    }

    private Gateway Create(FakeFirewall firewall,
        Func<CoreRequest, IPAddress, CancellationToken, Task<ProbeReachability>> check,
        TimeProvider? time = null, string? boot = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        ServiceJournal? journal = null)
    {
        var clock = time ?? TimeProvider.System;
        var file = typeof(Gateway).Assembly.Location;
        return new Gateway(new GatewayOptions(file, true, new Uri("https://example.com/"), SingBoxPath: file,
            KillSwitch: firewall, TunnelNetns: Path.Combine(_root, "no-network-namespace"), BootBlock: boot,
            Resolve: resolve, Preflight: check), clock, journal ?? new ServiceJournal(clock));
    }

    private static V1.ConnectRequest Request(string host, string id, int port = 443)
    {
        var definition = new ServerDefinition("test", string.Empty, ProxyProtocol.Vless, host, port, TransportOptions.Tcp,
            new SecurityOptions(TransportSecurity.Tls, Sni: "example.com"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: "11111111-2222-3333-4444-555555555555"));
        var settings = AppSettings.Default with { Protection = ProtectionSettings.Default with { CheckLeaksAfterConnect = false, BootProtection = false } };
        return new V1.ConnectRequest
        {
            Server = definition.ToContract(id), Mode = V1.Mode.Vpn, Protection = settings.ToContract(),
            Connection = settings.ToConnection(), Routing = settings.ToRouting(),
        };
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MissingInterfaceRoutes : IRouteTable
    {
        public IReadOnlyList<DefaultRoute> DefaultRoutes() => [new("pg-preflight-no-such-interface", false)];
    }

    private sealed class FakeFirewall : IKillSwitch
    {
        public string Name => "fake preflight firewall";
        public bool Enabled { get; set; }
        public bool FailOnEnable { get; set; }
        public bool IsEnabled => Enabled;
        public int Enables { get; private set; }
        public int Disables { get; private set; }
        public KillSwitchPolicy? LastPolicy { get; private set; }

        public void Enable(KillSwitchPolicy policy)
        {
            Enables++;
            LastPolicy = policy;
            if (FailOnEnable)
            {
                throw new IOException("fake firewall: stop before starting TUN");
            }

            Enabled = true;
        }

        public void Disable()
        {
            Disables++;
            Enabled = false;
        }
    }
}
