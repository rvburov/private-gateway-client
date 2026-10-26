using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Внешний IP и его провайдер (этап 7): какие адреса служба вообще спрашивает, разбор ответов RIPEstat на образцах формата,
/// память ответов, путь вопроса и вызовы через IPC. В интернет ни один тест не ходит: RIPEstat заменен подставным ответом.
/// Публичные адреса тестов — открытые резолверы и границы служебных диапазонов: их только классифицируют, о них не
/// спрашивают.
/// </summary>
public sealed class AddressLookupTests
{
    private static readonly IPAddress Public = IPAddress.Parse("1.1.1.1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("9.9.9.9", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::ffff:1.1.1.1", true)]
    [InlineData("11.0.0.1", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("223.255.255.254", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.254", false)]
    [InlineData("192.0.0.8", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.24", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::101:101", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]
    [InlineData("3fff::1", false)]
    public void Only_a_public_address_is_worth_asking_about(string address, bool isPublic) =>
        AddressLookup.IsPublic(IPAddress.Parse(address)).ShouldBe(isPublic);

    [Fact]
    public void Ripestat_answers_give_the_provider_network_and_place()
    {
        var address = IPAddress.Parse("203.0.113.24");
        AddressLookup.Parse(address, Overview, Geo).ShouldBe(
            new AddressInfo(address, 64500, "EXAMPLE-AS - Example Hosting B.V.", "203.0.113.0/24", "NL", "Amsterdam", null));

        AddressLookup.Parse(address, """{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.24"}}""", Geo)
            .ShouldBe(new AddressInfo(address, null, null, null, "NL", "Amsterdam", null), "сеть не объявлена — ни провайдера, ни сети, а место есть");
        AddressLookup.Parse(address, Overview, """
            {"status":"ok","data":{"located_resources":[{"resource":"203.0.113.0/24","locations":[{"country":"??","city":""},{"country":"de","city":" "}]}]}}
            """).ShouldBe(new AddressInfo(address, 64500, "EXAMPLE-AS - Example Hosting B.V.", "203.0.113.0/24", "DE", null, null),
            "«??» — не страна, пустой город — не город");
        AddressLookup.Parse(address, Overview, null).Country.ShouldBeNull("место не ответило — провайдер все равно есть");
        AddressLookup.Parse(address, """{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.0/24"}}""", Geo).Prefix
            .ShouldBeNull("сеть — только объявленная");
        AddressLookup.Parse(address, """{"status":"ok","data":{"announced":true,"asns":[{"asn":64500}],"resource":"203.0.113.24"}}""", null)
            .ShouldBe(new AddressInfo(address, 64500, null, null, null, null, null), "сам адрес вместо сети — не сеть; владельца нет — только номер");
    }

    [Fact]
    public void Without_answers_or_without_facts_the_reason_is_named()
    {
        var address = IPAddress.Parse("203.0.113.24");
        AddressLookup.Parse(address, null, null).Failure.ShouldBe(AddressFailure.Unreachable);
        AddressLookup.Parse(address, "<html>", "<html>").Failure.ShouldBe(AddressFailure.NotFound, "ответ был, но не о том — источник адреса не знает");
        AddressLookup.Parse(address, """{"status":"error","data":{"asns":[{"asn":64500}]}}""", null).Failure
            .ShouldBe(AddressFailure.NotFound, "ответ с ошибкой — не сведения");
        AddressLookup.Parse(address, """{"status":"ok","data":{"announced":true,"asns":[{"asn":0}],"resource":"203.0.113.0/24"}}""",
            """{"status":"ok","data":{"located_resources":[]}}""").Failure.ShouldBe(AddressFailure.NotFound);
    }

    [Fact]
    public async Task Private_address_is_answered_without_asking()
    {
        var lookup = new AddressLookup((_, _) => throw new InvalidOperationException("о частном адресе не спрашивают"), TimeProvider.System);

        (await lookup.DescribeAsync(IPAddress.Parse("192.168.1.10"), 0, null, Ct)).ShouldBe(AddressInfo.Failed(IPAddress.Parse("192.168.1.10"), AddressFailure.Private));
        (await lookup.DescribeAsync(IPAddress.Parse("::ffff:10.0.0.1"), 0, null, Ct)).Address.ShouldBe(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public async Task Request_names_only_the_address_and_goes_the_given_way()
    {
        var asked = new List<ServerProbe>();
        var lookup = new AddressLookup((probe, _) =>
        {
            lock (asked)
            {
                asked.Add(probe);
            }

            return Task.FromResult<string?>(probe.Target.AbsolutePath.Contains("prefix-overview", StringComparison.Ordinal) ? Overview : Geo);
        }, TimeProvider.System);
        var credentials = new NetworkCredential("probe", "11111111-2222-3333-4444-555555555555");

        var info = await lookup.DescribeAsync(IPAddress.Parse("2606:4700:4700:0::1111"), 10808, credentials, Ct);

        info.Asn.ShouldBe(64500);
        asked.Select(p => p.Target.AbsoluteUri).Order().ToList().ShouldBe([
            "https://stat.ripe.net/data/maxmind-geo-lite/data.json?resource=2606%3A4700%3A4700%3A%3A1111&sourceapp=private-gateway",
            "https://stat.ripe.net/data/prefix-overview/data.json?resource=2606%3A4700%3A4700%3A%3A1111&sourceapp=private-gateway",
        ], customMessage: "в запросе — только адрес в каноническом виде");
        asked.ShouldAllBe(p => p.Port == 10808 && p.Credentials == credentials && p.Timeout == AddressLookup.Timeout);
    }

    [Fact]
    public async Task Answer_is_kept_for_an_hour_and_silence_is_not_kept()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var silent = false;
        var lookup = new AddressLookup((probe, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(silent ? null : probe.Target.AbsolutePath.Contains("prefix-overview", StringComparison.Ordinal) ? Overview : Geo);
        }, time);

        (await lookup.DescribeAsync(Public, 0, null, Ct)).Asn.ShouldBe(64500);
        time.Advance(TimeSpan.FromMinutes(59));
        (await lookup.DescribeAsync(Public, 0, null, Ct)).Asn.ShouldBe(64500);
        calls.ShouldBe(2, "второй раз в течение часа — из памяти");

        time.Advance(TimeSpan.FromMinutes(1));
        silent = true;
        (await lookup.DescribeAsync(Public, 0, null, Ct)).Failure.ShouldBe(AddressFailure.Unreachable, "через час — спросить заново");
        silent = false;
        (await lookup.DescribeAsync(Public, 0, null, Ct)).Failure.ShouldBeNull("молчание не запомнено — спросили еще раз");
        calls.ShouldBe(6);
    }

    [Fact]
    public void Only_the_exit_address_of_the_proxy_mode_is_asked_through_the_core()
    {
        var link = new ProbeLink(10808, "probe", "secret");
        var exit = IPAddress.Parse("203.0.113.24");
        var connected = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Proxy,
            Exit = new ExitInfo("203.0.113.24", null, null, null, "198.51.100.7"),
        };

        Gateway.AddressRoute(connected, link, exit).ShouldBe(link, "об адресе выхода — с адреса выхода");
        Gateway.AddressRoute(connected, link, IPAddress.Parse("198.51.100.7")).ShouldBeNull("о реальном — напрямую, не связывая его с выходом");
        Gateway.AddressRoute(connected with { Mode = ConnectionMode.Vpn }, link, exit).ShouldBeNull("в режиме VPN путь один — туннель");
        Gateway.AddressRoute(connected with { CaptivePauseUntil = DateTimeOffset.UnixEpoch }, link, exit).ShouldBeNull("на паузе ядро в стороне");
        Gateway.AddressRoute(connected with { State = ConnectionState.Reconnecting }, link, exit).ShouldBeNull();
    }

    [Fact]
    public void Address_info_goes_through_the_contract_and_comes_back_the_same()
    {
        var info = new AddressInfo(IPAddress.Parse("2001:db8::24"), 64500, "Example Hosting B.V.", "2001:db8::/32", "NL", "Amsterdam", null);
        info.ToContract().ToCore().ShouldBe(info);
        new AddressInfo(Public, null, null, null, null, null, null).ToContract().ToCore().ShouldBe(new AddressInfo(Public, null, null, null, null, null, null));
        foreach (var failure in Enum.GetValues<AddressFailure>())
        {
            AddressInfo.Failed(Public, failure).ToContract().ToCore().ShouldBe(AddressInfo.Failed(Public, failure));
        }

        Should.Throw<ArgumentException>(() => new V1.AddressInfo { Address = "example.com" }.ToCore());
    }

    [Fact]
    public async Task Fetch_returns_the_body_and_silence_is_null()
    {
        using var site = new LocalSite(Overview);
        using var refused = new LocalSite("<html>slow down</html>", status: "429 Too Many Requests");
        var dead = new Uri($"http://127.0.0.1:{XrayKit.FreePort()}/");

        (await AddressLookup.FetchAsync(new ServerProbe(0, null, site.Url, TimeSpan.FromSeconds(5)), Ct)).ShouldBe(Overview);
        (await AddressLookup.FetchAsync(new ServerProbe(0, null, dead, TimeSpan.FromSeconds(5)), Ct)).ShouldBeNull();
        (await AddressLookup.FetchAsync(new ServerProbe(0, null, refused.Url, TimeSpan.FromSeconds(5)), Ct))
            .ShouldBeNull("отказ источника — не ответ: спросить можно будет снова");
    }

    [Fact]
    public async Task Service_describes_an_address_through_ipc_and_refuses_a_name()
    {
        var asked = 0;
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            AddressFetch = (probe, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<string?>(probe.Port == 0 && probe.Target.AbsolutePath.Contains("prefix-overview", StringComparison.Ordinal) ? Overview : Geo);
            },
        });
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);

        var info = await client.DescribeAddressAsync(Public, Ct);

        info.ShouldBe(new AddressInfo(Public, 64500, "EXAMPLE-AS - Example Hosting B.V.", "203.0.113.0/24", "NL", "Amsterdam", null),
            "защита выключена — обычным путем службы");
        asked.ShouldBe(2);
        var refused = await Should.ThrowAsync<RpcException>(async () =>
            await service.Client.DescribeAddressAsync(new V1.DescribeAddressRequest { Address = "example.com" }, cancellationToken: Ct));
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument, "имя служба не ищет ради окна");
    }

    [Fact]
    public async Task Measure_with_protection_off_finds_the_real_ip_directly()
    {
        using var direct = new LocalSite("fl=1\nip=198.51.100.7\nts=1\n");
        await using var service = await RunningService.StartAsync(options: o => o with { Trace = direct.Url });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);
        service.Gateway.Status.Exit.DirectIp.ShouldBeNull("выключенная защита сама никуда не ходит");

        await client.MeasureIpAsync(Ct);

        (await ServiceIpcTests.ReadUntilAsync(watch, s => s.Exit?.DirectIp == "198.51.100.7"))[^1].State.ShouldBe(V1.ConnectionState.Off);
        direct.Requests.ShouldBe(1);
    }

    // Образцы ответов RIPEstat — по формату документации (stat.ripe.net/docs), значения — из диапазонов документации.
    private const string Overview = """
        {"messages":[],"data_call_name":"prefix-overview","data":{"is_less_specific":false,"announced":true,
         "asns":[{"asn":64500,"holder":"EXAMPLE-AS - Example Hosting B.V."}],"related_prefixes":[],"resource":"203.0.113.0/24",
         "type":"prefix","block":{"resource":"203.0.0.0/8","desc":"APNIC (Status: ALLOCATED)","name":"IANA IPv4 Address Space Registry"},
         "actual_num_related":0,"query_time":"2026-10-26T12:00:00","num_filtered_out":0},"status":"ok","status_code":200}
        """;

    private const string Geo = """
        {"messages":[],"data_call_name":"maxmind-geo-lite","data":{"located_resources":[{"resource":"203.0.113.0/24",
         "locations":[{"country":"NL","city":"Amsterdam","resources":["203.0.113.0/24"],"latitude":52.37,"longitude":4.89,"covered_percentage":100.0}]}],
         "result_time":"2026-10-01T00:00:00"},"status":"ok","status_code":200}
        """;
}
