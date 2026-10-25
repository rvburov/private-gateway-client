using System.Text.Json.Nodes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Проверка утечек в режиме прокси с настоящими ядрами Xray: проверочный адрес — свой сайт с ответом
/// «ip=…», как у Cloudflare; через сервер теста отвечает другой сайт — «IP сервера». Из машины ничего не уходит.
/// </summary>
public sealed class LeakCheckTests
{
    private const string ServerIp = "203.0.113.24";
    private const string RealIp = "198.51.100.7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Trace_ip_is_the_first_ip_line_of_the_answer()
    {
        LatencyProbe.TraceIp("fl=1f1\nh=1.1.1.1\nip=203.0.113.24\nts=1.5\n").ShouldBe("203.0.113.24");
        LatencyProbe.TraceIp("ip=2001:db8::1\r\nloc=NL\r\n").ShouldBe("2001:db8::1");
        LatencyProbe.TraceIp("ip=not-an-address\nip=198.51.100.7\n").ShouldBe("198.51.100.7");
        LatencyProbe.TraceIp("h=1.1.1.1\nvisit_scheme=https\n").ShouldBeNull();
        LatencyProbe.TraceIp("<html>ip=</html>").ShouldBeNull();
    }

    [Theory]
    [InlineData("203.0.113.24", "2001:db8:24::24", true)]
    [InlineData("203.0.113.24", null, false)]
    [InlineData("203.0.113.24", "203.0.113.24", false)]
    [InlineData(null, "2001:db8:24::24", true)]
    [InlineData(null, null, null)]
    public void Server_opens_ipv6_only_if_it_answers_with_an_ipv6_address(string? ip4, string? ip6, bool? opens)
    {
        // По IPv4 ответил, а по IPv6 нет — у сервера нет IPv6; не ответил ни по одному — медленным бывает и сервер: вывода нет.
        Gateway.Ipv6Verdict(ip4, ip6).ShouldBe(opens);
    }

    [Fact]
    public async Task Exit_ip_without_a_proxy_is_asked_directly_and_silence_is_null()
    {
        using var site = new LocalSite("fl=1\nip=" + RealIp + "\n");
        var dead = new Uri($"http://127.0.0.1:{XrayKit.FreePort()}/");

        (await LatencyProbe.ExitIpAsync(new ServerProbe(0, null, site.Url, TimeSpan.FromSeconds(5)), Ct)).ShouldBe(RealIp);
        (await LatencyProbe.ExitIpAsync(new ServerProbe(0, null, dead, TimeSpan.FromSeconds(5)), Ct)).ShouldBeNull();
        site.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task After_connect_sites_see_the_server_ip_and_not_the_real_one()
    {
        await using var stand = await Stand.CreateAsync(real: RealIp);
        using var watch = stand.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await stand.Service.Client.ConnectAsync(ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort()), cancellationToken: Ct);

        var measured = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.Ip == ServerIp))[^1];
        measured.State.ShouldBe(V1.ConnectionState.Connected);
        measured.Exit.DirectIp.ShouldBe(RealIp, "в режиме прокси прямой путь открыт — реальный IP измерен напрямую");
        measured.Protection.CheckedAt.ShouldNotBeNull("«Проверено» на главном экране");
        stand.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e => e.Message == "Проверка утечек: сайты видят IP сервера, а не ваш");

        var report = await stand.Service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct);
        report.Results.Select(r => r.Id).ShouldBe(
            Enum.GetValues<DiagnosticCheck>().Where(c => c != DiagnosticCheck.OtherVpn).Select(c => c.ToContractId()), ignoreOrder: true,
            "все проверки «Проверки», кроме чужого VPN — у службы теста нет маршрутов");
        report.Results.Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId()).State.ShouldBe(V1.CheckState.Ok);
        report.Results.Single(r => r.Id == DiagnosticCheck.KillSwitch.ToContractId()).State.ShouldBe(V1.CheckState.Warning, "в режиме прокси блока нет");
        stand.Exit.Requests.ShouldBeGreaterThanOrEqualTo(2, "после подключения и по просьбе — через сервер");
    }

    [Fact]
    public async Task Real_ip_through_the_server_is_a_leak()
    {
        await using var stand = await Stand.CreateAsync(real: ServerIp);
        using var watch = stand.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await stand.Service.Client.ConnectAsync(ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.Ip == ServerIp);

        var exit = (await stand.Service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId());
        exit.State.ShouldBe(V1.CheckState.Warning);
        exit.Reason.ShouldBe("same_ip");
        stand.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
            e.Level == JournalLevel.Error && e.Message == "Проверка утечек: IP для сайтов совпадает с реальным — трафик идет мимо туннеля");
    }

    [Fact]
    public async Task Without_the_setting_the_ip_is_measured_only_on_request()
    {
        await using var stand = await Stand.CreateAsync(real: RealIp);
        using var watch = stand.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await stand.Service.Client.ConnectAsync(ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.Ip == ServerIp);
        var request = ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort());
        request.Protection.CheckLeaksAfterConnect = false;

        await stand.Service.Client.ConnectAsync(request, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connecting);
        var connected = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected))[^1];
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        connected.Exit.Ip.ShouldBeEmpty("IP прежнего подключения не выдается за нынешний");
        connected.Exit.DirectIp.ShouldBe(RealIp, "реальный IP — тот же");
        stand.Service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message.StartsWith("Проверка утечек", StringComparison.Ordinal))
            .ShouldBe(1, "после второго подключения проверки не было");
        stand.Direct.Requests.ShouldBe(1);

        var exit = (await stand.Service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId());

        exit.State.ShouldBe(V1.CheckState.Ok);
        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.Ip == ServerIp))[^1].Exit.DirectIp.ShouldBe(RealIp);
        stand.Direct.Requests.ShouldBe(2, "в режиме прокси реальный IP мерится заново при каждой проверке");
    }

    [Fact]
    public async Task Silent_check_address_is_no_answer_not_ok()
    {
        await using var stand = await Stand.CreateAsync(real: RealIp, trace: new Uri($"http://127.0.0.1:{XrayKit.FreePort()}/"), exitPage: "без адреса");
        using var watch = stand.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await stand.Service.Client.ConnectAsync(ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        var exit = (await stand.Service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: Ct)).Results
            .Single(r => r.Id == DiagnosticCheck.ExitIp.ToContractId());
        exit.State.ShouldBe(V1.CheckState.Warning);
        exit.Reason.ShouldBe("no_answer");
        stand.Service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(e =>
            e.Message == "Проверка утечек: проверочный адрес не ответил через сервер — IP для сайтов не измерен");
        var status = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.Protection.CheckedAt is not null))[^1];
        status.Exit.Ip.ShouldBeEmpty("IP не измерен — не выдумывать");
    }

    [Fact]
    public async Task After_connect_the_check_tries_twice_before_saying_no_answer()
    {
        await using var stand = await Stand.CreateAsync(real: RealIp, exitPage: "без адреса");
        using var watch = stand.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await stand.Service.Client.ConnectAsync(ServiceIpcTests.Request(stand.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        const string Silence = "Проверка утечек: проверочный адрес не ответил через сервер — IP для сайтов не измерен";
        for (var i = 0; i < 100 && !stand.Service.Journal.Tail(ServiceJournal.Capacity).Any(e => e.Message == Silence); i++)
        {
            await Task.Delay(100, Ct);
        }

        stand.Service.Journal.Tail(ServiceJournal.Capacity).Count(e => e.Message == Silence).ShouldBe(1, "о молчании — один раз, после второй попытки");
        stand.Exit.Requests.ShouldBe(2, "первый запрос через новый туннель бывает долгим — вторая попытка через 3 с");
    }

    /// <summary>
    /// Сервер Xray с выходом на сайт «IP сервера» и служба, у которой проверочный адрес — сайт «реальный IP»;
    /// через сервер любой адрес приходит на первый.
    /// </summary>
    private sealed class Stand : IAsyncDisposable
    {
        private readonly CoreProcess _server;

        private Stand(LocalSite exit, LocalSite direct, Core.Servers.ServerDefinition definition, CoreProcess server, RunningService service)
        {
            (Exit, Direct, Definition, _server, Service) = (exit, direct, definition, server, service);
        }

        public LocalSite Exit { get; }

        public LocalSite Direct { get; }

        public Core.Servers.ServerDefinition Definition { get; }

        public RunningService Service { get; }

        /// <param name="real">Какой IP проверочный адрес называет напрямую.</param>
        /// <param name="trace">Проверочный адрес; null — сайт «реальный IP».</param>
        /// <param name="exitPage">Ответ через сервер; null — «ip=» адреса сервера.</param>
        public static async Task<Stand> CreateAsync(string real, Uri? trace = null, string? exitPage = null)
        {
            Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
            var exit = new LocalSite(exitPage ?? $"fl=1\nip={ServerIp}\nts=1\n");
            var direct = new LocalSite($"fl=1\nip={real}\nts=1\n");
            var serverPort = XrayKit.FreePort();
            var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
            serverConfig["outbounds"] = new JsonArray(
                new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{exit.Url.Port}" } });
            var server = await XrayKit.Start(serverConfig, serverPort);
            var service = await RunningService.StartAsync(exit.Url, options: o => o with { Trace = trace ?? direct.Url });
            return new Stand(exit, direct, definition, server, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _server.Dispose();
            Exit.Dispose();
            Direct.Dispose();
        }
    }
}
