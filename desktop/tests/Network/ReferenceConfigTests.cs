using System.Diagnostics;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Регрессия этапа 11: конфиг каждой эталонной ссылки shared/testdata/links читает само ядро — <c>xray run -test</c> в
/// режимах прокси и VPN и в замере задержки, <c>sing-box check</c> у ретранслятора Hysteria2 и TUIC. Тесты конфига в Unit
/// сверяют поля, а это — что ядро закрепленной версии принимает их все: имя поля или значение, которых оно не знает,
/// падает здесь, а не у пользователя на «Подключении».
/// </summary>
public sealed class ReferenceConfigTests
{
    private static readonly string Links = Path.Combine(XrayKit.Root, "shared", "testdata", "links");

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var (fixture, _) in Servers())
        {
            data.Add(fixture);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Xray_accepts_the_config_of_each_reference_link_in_proxy_and_vpn_mode(string fixture)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var server = Servers().Single(s => s.Fixture == fixture).Server;
        var relay = XrayConfigBuilder.NeedsRelay(server) ? new RelayLink(31500, "relay-user", "relay-pass") : null;
        var plain = new CoreRequest(server, ConnectionMode.Proxy, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
        var proxy = XrayConfigBuilder.Build(plain, relay: relay);
        // Транспорт, которого у протокола в Xray нет, — отказ до ядра, и он должен быть единственной причиной.
        if (proxy.Rejection is { } rejection)
        {
            rejection.ShouldBe(CoreRejection.UnsupportedTransport, fixture);
            return;
        }

        Accepted(Xray(proxy.Config!), fixture + ", режим прокси");
        Accepted(Xray(XrayConfigBuilder.Build(Everything(plain, DnsChoice.CloudflareDoh), relay: relay).Config.ShouldNotBeNull()),
            fixture + ", режим прокси, все включено");
        var tun = new TunLink(31000, "tun-user", "tun-pass", "198.51.100.20");
        var probe = new ProbeLink(31001, "probe-user", "probe-pass");
        var vpn = plain with { Mode = ConnectionMode.Vpn };
        Accepted(Xray(XrayConfigBuilder.Build(vpn, tun, probe, relay).Config.ShouldNotBeNull()), fixture + ", режим VPN");
        Accepted(Xray(XrayConfigBuilder.Build(Everything(vpn, DnsChoice.Custom), tun, probe, relay).Config.ShouldNotBeNull()),
            fixture + ", режим VPN, все включено");
    }

    /// <summary>
    /// Все, что по умолчанию выключено и меняет конфиг: FakeDNS, свой DNS в локальной сети или DoH, IPv6, локальная сеть,
    /// отпечаток, Mux, фрагментация TLS, подробный журнал, раздельный режим с правилами сайтов и категорией геобазы.
    /// </summary>
    private static CoreRequest Everything(CoreRequest request, DnsChoice dns) => request with
    {
        Protection = ProtectionSettings.Default with
        {
            Dns = dns,
            CustomDns = dns == DnsChoice.Custom ? "192.168.1.53" : null,
            FakeDns = true,
            BlockIpv6 = false,
            Addresses = AddressPreference.PreferIpv6,
            AllowLan = true,
            LanDiscovery = true,
        },
        Connection = ConnectionSettings.Default with
        {
            CoreLog = CoreLogLevel.Debug,
            Fingerprint = TlsFingerprint.Random,
            Mux = true,
            TlsFragment = true,
            ProxyAllowLan = true,
        },
        Routing = new RoutingSettings(RoutingMode.Split, [], [
            new DomainRule("example.com", RuleAction.Direct),
            new DomainRule("*.example.net", RuleAction.Block),
            new DomainRule("example.org", RuleAction.Proxy),
            new DomainRule("geosite:category-ads-all", RuleAction.Block),
        ]),
    };

    [Fact]
    public void Xray_accepts_one_latency_config_for_all_reference_links()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var servers = Servers().Select(s => s.Server).ToList();
        var relays = servers.Select((s, i) => XrayConfigBuilder.NeedsRelay(s) ? new RelayLink(32000 + i, "relay-user", "relay-pass") : null).ToList();
        var probe = XrayConfigBuilder.BuildProbe(servers, [.. servers.Select((_, i) => 33000 + i)], ConnectionSettings.Default with { Mux = true },
            relays, new SocksAccount("probe-user", "probe-pass"));

        probe.Rejections.Count(r => r is null).ShouldBeGreaterThan(servers.Count / 2, "замеряется большинство эталонов");
        Accepted(Xray(probe.Config.ShouldNotBeNull()), "замер задержки всех эталонов");
    }

    [Fact]
    public void Sing_box_accepts_the_relay_for_every_hysteria2_and_tuic_reference_link()
    {
        Assert.SkipWhen(XrayKit.SingBox is null, "нет сборки sing-box — scripts/get-singbox.sh");
        var routes = Servers().Select(s => s.Server).Where(XrayConfigBuilder.NeedsRelay)
            .Select((s, i) => new RelayRoute(s, new RelayLink(31500 + i, "relay-user", "relay-pass")))
            .ToList();
        routes.Select(r => r.Server.Protocol).Distinct().Order().ShouldBe([ProxyProtocol.Hysteria2, ProxyProtocol.Tuic]);

        Accepted(Run(XrayKit.SingBox!, ["check", "-c", "stdin"], SingBoxConfigBuilder.BuildRelay(routes), null), "ретранслятор sing-box");
    }

    private static IEnumerable<(string Fixture, ServerDefinition Server)> Servers()
    {
        foreach (var file in Directory.EnumerateFiles(Links, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (LinkParser.Parse(File.ReadAllText(file)) is { IsOk: true } parsed)
            {
                yield return (Path.GetRelativePath(Links, file).Replace('\\', '/')[..^".txt".Length], parsed.Server!);
            }
        }
    }

    private static (int Code, string Output) Xray(JsonObject config)
    {
        var folder = Path.GetDirectoryName(XrayKit.Xray!)!;
        return Run(XrayKit.Xray!, ["run", "-test", "-config", "stdin:"], config, new Dictionary<string, string> { ["XRAY_LOCATION_ASSET"] = folder });
    }

    private static void Accepted((int Code, string Output) result, string what) =>
        result.Code.ShouldBe(0, $"{what}: ядро не приняло конфиг\n{result.Output}");

    /// <summary>Ядро с конфигом в stdin; код выхода и весь вывод — в сообщение при отказе.</summary>
    private static (int Code, string Output) Run(string executable, string[] arguments, JsonObject config, Dictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? [])
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Write(config.ToJsonString());
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            throw new TimeoutException($"{Path.GetFileName(executable)} не ответил за минуту");
        }

        return (process.ExitCode, output.Result + errors.Result);
    }
}
