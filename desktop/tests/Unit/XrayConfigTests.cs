using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Конфиг Xray-core из эталонных ссылок shared/testdata и настроек.</summary>
public sealed class XrayConfigTests
{
    private static readonly string Links = Path.Combine(FindRoot(), "shared", "testdata", "links");

    [Fact]
    public void Vless_reality_vision_becomes_a_vless_outbound_with_reality()
    {
        var config = Build(Link("vless/reality-vision"));

        var proxy = Outbound(config, XrayConfigBuilder.ProxyTag);
        proxy["protocol"]!.GetValue<string>().ShouldBe("vless");
        var server = proxy["settings"]!["vnext"]![0]!;
        (server["address"]!.GetValue<string>(), server["port"]!.GetValue<int>()).ShouldBe(("203.0.113.24", 443));
        var user = server["users"]![0]!;
        (user["id"]!.GetValue<string>(), user["encryption"]!.GetValue<string>(), user["flow"]!.GetValue<string>())
            .ShouldBe(("11111111-2222-3333-4444-555555555555", "none", "xtls-rprx-vision"));
        var stream = proxy["streamSettings"]!;
        (stream["network"]!.GetValue<string>(), stream["security"]!.GetValue<string>()).ShouldBe(("raw", "reality"));
        var reality = stream["realitySettings"]!;
        reality["serverName"]!.GetValue<string>().ShouldBe("www.example.com");
        reality["fingerprint"]!.GetValue<string>().ShouldBe("chrome");
        reality["publicKey"]!.GetValue<string>().ShouldBe("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        reality["shortId"]!.GetValue<string>().ShouldBe("0123abcd");

        var link = Link("vless/reality-vision");
        var noFingerprint = link with { Security = link.Security with { Fingerprint = null } };
        Outbound(Build(noFingerprint), XrayConfigBuilder.ProxyTag)["streamSettings"]!["realitySettings"]!["fingerprint"]!.GetValue<string>()
            .ShouldBe("chrome", "REALITY без отпечатка не работает");
    }

    [Fact]
    public void Only_the_chosen_server_is_an_exit_without_balancer_or_fallback()
    {
        var config = Build(Link("vless/reality-vision"), connection: ConnectionSettings.Default with { TlsFragment = true, Mux = true });

        // Наружу через сервер — только выбранный; остальные выходы — напрямую (freedom) или блок.
        var outbounds = config["outbounds"]!.AsArray().Select(o => o!.AsObject()).ToList();
        outbounds.Where(o => o["protocol"]!.GetValue<string>() is not ("freedom" or "blackhole"))
            .ShouldHaveSingleItem()["tag"]!.GetValue<string>().ShouldBe(XrayConfigBuilder.ProxyTag);
        outbounds.Single(o => o["tag"]!.GetValue<string>() == XrayConfigBuilder.ProxyTag)["settings"]!["vnext"]!.AsArray()
            .ShouldHaveSingleItem()!["address"]!.GetValue<string>().ShouldBe("203.0.113.24");
        config.ContainsKey("observatory").ShouldBeFalse("без проверок, по которым ядро само выбрало бы другой выход");
        config.ContainsKey("burstObservatory").ShouldBeFalse();
        config["routing"]!.AsObject().ContainsKey("balancers").ShouldBeFalse();
        config["routing"]!["rules"]!.AsArray().ShouldAllBe(r => r!.AsObject().ContainsKey("outboundTag") && !r.AsObject().ContainsKey("balancerTag"));
    }

    [Fact]
    public void Probe_config_pairs_each_local_inbound_with_its_own_server()
    {
        var probe = XrayConfigBuilder.BuildProbe(
            [Link("vless/reality-vision"), Link("hysteria2/hy2-short-scheme"), Link("trojan/ws")],
            [20001, 20002, 20003],
            ConnectionSettings.Default with { TlsFragment = true });

        probe.Rejections.ShouldBe([null, CoreRejection.UnsupportedProtocol, null]);
        var config = probe.Config.ShouldNotBeNull();
        var inbounds = config["inbounds"]!.AsArray();
        inbounds.Select(i => (i!["port"]!.GetValue<int>(), i["listen"]!.GetValue<string>(), i["protocol"]!.GetValue<string>()))
            .ShouldBe([(20001, "127.0.0.1", "socks"), (20003, "127.0.0.1", "socks")]);
        var rules = config["routing"]!["rules"]!.AsArray()
            .Select(r => (r!["inboundTag"]![0]!.GetValue<string>(), r["outboundTag"]!.GetValue<string>())).ToList();
        rules.ShouldBe([("probe-in-0", "probe-out-0"), ("probe-in-2", "probe-out-2")]);
        Outbound(config, "probe-out-0")["settings"]!["vnext"]![0]!["address"]!.GetValue<string>().ShouldBe("203.0.113.24");
        Outbound(config, "probe-out-2")["protocol"]!.GetValue<string>().ShouldBe("trojan");
        Outbound(config, "probe-out-2")["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>().ShouldBe(XrayConfigBuilder.FragmentTag);
        Outbound(config, XrayConfigBuilder.FragmentTag)["protocol"]!.GetValue<string>().ShouldBe("freedom");
        config["log"]!["access"]!.GetValue<string>().ShouldBe("none");

        XrayConfigBuilder.BuildProbe([Link("tuic/bbr")], [20001], ConnectionSettings.Default).Config.ShouldBeNull();
    }

    [Fact]
    public void Tls_transports_carry_path_host_alpn_and_service_name()
    {
        var xhttp = Outbound(Build(Link("vless/tls-xhttp")), XrayConfigBuilder.ProxyTag)["streamSettings"]!;
        xhttp["network"]!.GetValue<string>().ShouldBe("xhttp");
        (xhttp["xhttpSettings"]!["path"]!.GetValue<string>(), xhttp["xhttpSettings"]!["host"]!.GetValue<string>()).ShouldBe(("/xh", "cdn.example.org"));
        xhttp["tlsSettings"]!["serverName"]!.GetValue<string>().ShouldBe("example.org");
        xhttp["tlsSettings"]!["alpn"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["h2", "http/1.1"]);

        var grpc = Outbound(Build(Link("vless/grpc-raw-utf8-name")), XrayConfigBuilder.ProxyTag)["streamSettings"]!;
        (grpc["network"]!.GetValue<string>(), grpc["grpcSettings"]!["serviceName"]!.GetValue<string>()).ShouldBe(("grpc", "grpc-svc"));

        var trojan = Outbound(Build(Link("trojan/ws")), XrayConfigBuilder.ProxyTag);
        trojan["protocol"]!.GetValue<string>().ShouldBe("trojan");
        trojan["settings"]!["servers"]![0]!["password"]!.GetValue<string>().ShouldBe("pw-trojan-1");
        trojan["streamSettings"]!["wsSettings"]!["path"]!.GetValue<string>().ShouldBe("/tr");
    }

    [Fact]
    public void Vmess_shadowsocks_and_wireguard_have_their_own_settings()
    {
        var vmess = Outbound(Build(Link("vmess/ws-tls")), XrayConfigBuilder.ProxyTag);
        vmess["protocol"]!.GetValue<string>().ShouldBe("vmess");
        vmess["settings"]!["vnext"]![0]!["users"]![0]!["security"]!.GetValue<string>().ShouldBe("auto");
        vmess["streamSettings"]!["tlsSettings"]!["fingerprint"]!.GetValue<string>().ShouldBe("firefox");

        var ss = Outbound(Build(Link("shadowsocks/2022-plain-userinfo")), XrayConfigBuilder.ProxyTag);
        var ssServer = ss["settings"]!["servers"]![0]!;
        (ssServer["method"]!.GetValue<string>(), ssServer["password"]!.GetValue<string>()).ShouldBe(("2022-blake3-aes-128-gcm", "aaaaaaaaaaaaaaaaaaaaaa=="));
        ss["streamSettings"]!["security"]!.GetValue<string>().ShouldBe("none");

        var wg = Outbound(Build(Link("wireguard/basic")), XrayConfigBuilder.ProxyTag);
        wg["protocol"]!.GetValue<string>().ShouldBe("wireguard");
        wg["streamSettings"].ShouldBeNull();
        var settings = wg["settings"]!;
        settings["secretKey"]!.GetValue<string>().ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=");
        settings["peers"]![0]!["endpoint"]!.GetValue<string>().ShouldBe("192.0.2.33:51820");
        settings["address"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["10.0.0.2/32", "fd00::2/128"]);
        settings["mtu"]!.GetValue<int>().ShouldBe(1420);
    }

    [Fact]
    public void Hysteria2_and_tuic_wait_for_the_second_core()
    {
        XrayConfigBuilder.Build(Request(Link("hysteria2/hy2-short-scheme"))).ShouldBe(new XrayConfigResult(null, CoreRejection.UnsupportedProtocol));
        XrayConfigBuilder.Build(Request(Link("tuic/bbr"))).Rejection.ShouldBe(CoreRejection.UnsupportedProtocol);
        var noKey = Link("vless/reality-vision") with { Credentials = new ServerCredentials() };
        XrayConfigBuilder.Build(Request(noKey)).Rejection.ShouldBe(CoreRejection.MissingCredentials);
        var quic = Link("vless/reality-vision") with { Transport = TransportOptions.Quic };
        XrayConfigBuilder.Build(Request(quic)).Rejection.ShouldBe(CoreRejection.UnsupportedTransport);
    }

    [Fact]
    public void Inbound_is_local_unless_the_proxy_is_opened_to_the_lan()
    {
        var vpn = Build(Link("vless/reality-vision"), mode: ConnectionMode.Vpn, connection: ConnectionSettings.Default with { ProxyAllowLan = true });
        var inbound = vpn["inbounds"]![0]!;
        (inbound["protocol"]!.GetValue<string>(), inbound["port"]!.GetValue<int>(), inbound["listen"]!.GetValue<string>())
            .ShouldBe(("socks", 10808, "127.0.0.1"), "в режиме VPN вход только для своего TUN");
        inbound["sniffing"]!["routeOnly"]!.GetValue<bool>().ShouldBeTrue();

        var lan = Build(Link("vless/reality-vision"), mode: ConnectionMode.Proxy, connection: ConnectionSettings.Default with { ProxyAllowLan = true, ProxyPort = 2080 });
        (lan["inbounds"]![0]!["listen"]!.GetValue<string>(), lan["inbounds"]![0]!["port"]!.GetValue<int>()).ShouldBe(("0.0.0.0", 2080));

        var local = Build(Link("vless/reality-vision"), mode: ConnectionMode.Proxy);
        local["inbounds"]![0]!["listen"]!.GetValue<string>().ShouldBe("127.0.0.1");
    }

    [Fact]
    public void Log_never_records_sites()
    {
        var config = Build(Link("vless/reality-vision"), connection: ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug });

        config["log"]!["access"]!.GetValue<string>().ShouldBe("none");
        config["log"]!["dnsLog"]!.GetValue<bool>().ShouldBeFalse();
        config["log"]!["loglevel"]!.GetValue<string>().ShouldBe("debug");
        Build(Link("vless/reality-vision"))["log"]!["loglevel"]!.GetValue<string>().ShouldBe("warning");
    }

    [Fact]
    public void Dns_follows_protection_settings()
    {
        Servers(Build(Link("vless/reality-vision"))).ShouldBe([XrayConfigBuilder.DefaultDns]);
        Servers(Build(Link("vless/reality-vision"), protection: ProtectionSettings.Default with { Dns = DnsChoice.Quad9Doh }))
            .ShouldBe(["https://9.9.9.9/dns-query"]);
        var custom = Build(Link("vless/reality-vision"), protection: ProtectionSettings.Default with
        {
            Dns = DnsChoice.Custom, CustomDns = "192.0.2.53", Addresses = AddressPreference.Ipv4Only,
        });
        Servers(custom).ShouldBe(["192.0.2.53"]);
        custom["dns"]!["queryStrategy"]!.GetValue<string>().ShouldBe("UseIPv4");
        Outbound(custom, XrayConfigBuilder.DirectTag)["settings"]!["domainStrategy"]!.GetValue<string>().ShouldBe("UseIPv4");
    }

    [Theory]
    [InlineData(true, AddressPreference.PreferIpv4, "UseIP", false)]
    [InlineData(false, AddressPreference.PreferIpv4, "UseIP", false)]
    [InlineData(true, AddressPreference.PreferIpv6, "UseIP", false)]
    [InlineData(true, AddressPreference.Ipv4Only, "UseIPv4", true)]
    [InlineData(false, AddressPreference.Ipv4Only, "UseIPv4", true)]
    public void Ipv4_only_asks_dns_for_ipv4_only_and_blocks_ipv6_of_programs(bool blockIpv6, AddressPreference addresses, string strategy, bool closed)
    {
        var config = Build(Link("vless/reality-vision"), protection: ProtectionSettings.Default with { BlockIpv6 = blockIpv6, Addresses = addresses });

        // С AAAA в ответе правило «IPv6 — в блок» по адресам имени (IPIfNonMatch) закрыло бы и сайт, доступный по IPv4.
        config["dns"]!["queryStrategy"]!.GetValue<string>().ShouldBe(strategy);
        var ipv6 = config["routing"]!["rules"]!.AsArray().SingleOrDefault(r => r!["ip"] is JsonArray ip && ip[0]!.GetValue<string>() == "::/0");
        if (!closed)
        {
            ipv6.ShouldBeNull("IPv6 идет через сервер; без IPv6 у сервера его закрывает TUN, а не правило по адресам имени");
            return;
        }

        ipv6.ShouldNotBeNull()["outboundTag"]!.GetValue<string>().ShouldBe(XrayConfigBuilder.BlockTag);
        ipv6["inboundTag"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe([XrayConfigBuilder.InboundTag],
            "только программы: собственный DNS ядра, заданный адресом IPv6, идет через сервер");
    }

    [Fact]
    public void Rules_block_the_lan_and_ipv6_and_keep_direct_only_in_split_mode()
    {
        var rules = new[]
        {
            new DomainRule("example.com", RuleAction.Direct),
            new DomainRule("*.example.net", RuleAction.Block),
            new DomainRule("geosite:category-ads-all", RuleAction.Block),
            new DomainRule("example.org", RuleAction.Proxy),
            new DomainRule("198.51.100.0/24", RuleAction.Direct),
            new DomainRule("203.0.113.9", RuleAction.Block),
        };

        var all = Rules(Build(Link("vless/reality-vision"), routing: new RoutingSettings(RoutingMode.AllTraffic, [], rules)));
        all.ShouldBe(
        [
            ("ip:geoip:private", "block"),
            ("domain:domain:example.net", "block"), ("domain:geosite:category-ads-all", "block"), ("domain:domain:example.org", "proxy"),
            ("ip:203.0.113.9", "block"),
        ], "адрес и подсеть — условием IP, в порядке списка");

        var split = Rules(Build(Link("vless/reality-vision"),
            protection: ProtectionSettings.Default with { AllowLan = true, BlockIpv6 = false },
            routing: new RoutingSettings(RoutingMode.Split, [], rules)));
        split[0].ShouldBe(("ip:geoip:private", "direct"), "локальная сеть разрешена");
        split.ShouldContain(("domain:domain:example.com", "direct"));
        split.ShouldContain(("ip:198.51.100.0/24", "direct"));
        split.ShouldNotContain(("ip:::/0", "block"));
        Rules(Build(Link("vless/reality-vision"), protection: ProtectionSettings.Default with { Addresses = AddressPreference.Ipv4Only }))
            .ShouldContain(("ip:::/0", "block"), "«Только IPv4» — IPv6 закрыт и в правилах");
    }

    [Theory]
    [InlineData("192.168.1.53", true)]
    [InlineData("fd00::53", true)]
    [InlineData("9.9.9.9", false)]
    [InlineData("https://192.168.1.53/dns-query", false)]
    public void Own_dns_in_a_private_network_is_asked_directly_even_with_the_lan_closed(string custom, bool direct)
    {
        var config = Build(Link("vless/reality-vision"), protection: ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = custom });

        var rules = Rules(config);
        if (direct)
        {
            rules.IndexOf(("ip:" + custom, "direct")).ShouldBe(rules.IndexOf(("ip:geoip:private", "block")) - 1, "раньше закрытия локальной сети");
            config["routing"]!["rules"]!.AsArray().Single(r => r!["ip"]?[0]?.GetValue<string>() == custom)!["port"]!.GetValue<string>().ShouldBe("53");
        }
        else
        {
            rules.ShouldNotContain(r => r.Outbound == "direct");
        }
    }

    [Fact]
    public void Mux_and_tls_fragment_are_added_when_chosen_but_mux_not_with_vision()
    {
        var connection = ConnectionSettings.Default with { Mux = true, TlsFragment = true, Fingerprint = TlsFingerprint.Safari };

        var vision = Build(Link("vless/reality-vision"), connection: connection);
        Outbound(vision, XrayConfigBuilder.ProxyTag)["mux"].ShouldBeNull("Mux несовместим с XTLS Vision");
        Outbound(vision, XrayConfigBuilder.ProxyTag)["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>().ShouldBe(XrayConfigBuilder.FragmentTag);
        Outbound(vision, XrayConfigBuilder.FragmentTag)["settings"]!["fragment"]!["packets"]!.GetValue<string>().ShouldBe("tlshello");
        Outbound(vision, XrayConfigBuilder.ProxyTag)["streamSettings"]!["realitySettings"]!["fingerprint"]!.GetValue<string>().ShouldBe("safari");

        var trojan = Build(Link("trojan/ws"), connection: connection);
        Outbound(trojan, XrayConfigBuilder.ProxyTag)["mux"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        Build(Link("trojan/ws"))["outbounds"]!.AsArray().Select(o => o!["tag"]!.GetValue<string>()).ShouldBe(["proxy", "direct", "block"]);
    }

    [Theory]
    [InlineData("example.com", "domain:example.com")]
    [InlineData("*.example.com", "domain:example.com")]
    [InlineData("*.local", "domain:local")]
    [InlineData("geosite:category-ads-all", "geosite:category-ads-all")]
    public void Site_patterns_become_xray_conditions(string pattern, string condition)
    {
        XrayConfigBuilder.DomainCondition(pattern).ShouldBe(condition);
    }

    [Fact]
    public void Probe_inbound_goes_straight_to_the_server_past_every_rule()
    {
        var probe = new ProbeLink(32000, "probe-user", "probe-pass");
        var routing = new RoutingSettings(RoutingMode.Split, [], [new DomainRule("cp.cloudflare.com", RuleAction.Direct)]);
        var request = Request(Link("vless/reality-vision")) with { Routing = routing, Protection = ProtectionSettings.Default with { AllowLan = true } };

        foreach (var config in new[]
        {
            XrayConfigBuilder.Build(request, probe: probe).Config!,
            XrayConfigBuilder.Build(request, new TunLink(31000, "user-1", "pass-1", "198.51.100.20"), probe).Config!,
        })
        {
            var inbound = config["inbounds"]!.AsArray().Single(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.ProbeTag)!;
            (inbound["listen"]!.GetValue<string>(), inbound["port"]!.GetValue<int>(), inbound["settings"]!["auth"]!.GetValue<string>())
                .ShouldBe(("127.0.0.1", 32000, "password"), "вход проверки — только для службы");
            var first = config["routing"]!["rules"]![0]!;
            (first["inboundTag"]![0]!.GetValue<string>(), first["outboundTag"]!.GetValue<string>())
                .ShouldBe((XrayConfigBuilder.ProbeTag, XrayConfigBuilder.ProxyTag),
                    "первым правилом и прямо в сервер: ни «Напрямую» для проверочного адреса, ни локальная сеть пробу не обманут");
        }

        XrayConfigBuilder.Build(request).Config!["inbounds"]!.AsArray().ShouldHaveSingleItem("без пробы — один вход");
    }

    [Fact]
    public void Tun_link_takes_only_its_own_tun_and_dials_the_resolved_address()
    {
        var tun = new TunLink(31000, "user-1", "pass-1", "198.51.100.20");
        var server = Link("vless/reality-vision");

        var config = XrayConfigBuilder.Build(Request(server), tun).Config.ShouldNotBeNull();

        var inbound = config["inbounds"]!.AsArray().ShouldHaveSingleItem()!;
        (inbound["listen"]!.GetValue<string>(), inbound["port"]!.GetValue<int>(), inbound["settings"]!["auth"]!.GetValue<string>())
            .ShouldBe(("127.0.0.1", 31000, "password"), "вход только для своего TUN: на 127.0.0.1 и с паролем на подключение");
        var account = inbound["settings"]!["accounts"]![0]!;
        (account["user"]!.GetValue<string>(), account["pass"]!.GetValue<string>()).ShouldBe(("user-1", "pass-1"));
        var proxy = Outbound(config, XrayConfigBuilder.ProxyTag);
        proxy["settings"]!["vnext"]![0]!["address"]!.GetValue<string>().ShouldBe("198.51.100.20");
        proxy["streamSettings"]!["realitySettings"]!["serverName"]!.GetValue<string>()
            .ShouldBe(server.Security.Sni, "имя для TLS — прежнее, меняется только адрес соединения");
        Outbound(config, XrayConfigBuilder.DnsTag)["protocol"]!.GetValue<string>().ShouldBe("dns");
        var dns = config["routing"]!["rules"]![0]!;
        (dns["port"]!.GetValue<string>(), dns["outboundTag"]!.GetValue<string>(), dns["inboundTag"]![0]!.GetValue<string>())
            .ShouldBe(("53", XrayConfigBuilder.DnsTag, XrayConfigBuilder.InboundTag),
                "DNS из TUN — первым правилом, раньше блока локальной сети: запрос к роутеру тоже отвечает Xray");
        Build(server)["outbounds"]!.AsArray().ShouldNotContain(o => o!["tag"]!.GetValue<string>() == XrayConfigBuilder.DnsTag, "в режиме прокси DNS спрашивают программы сами");

        var trojan = Outbound(XrayConfigBuilder.Build(Request(Link("trojan/ws")), tun).Config!, XrayConfigBuilder.ProxyTag);
        trojan["settings"]!["servers"]![0]!["address"]!.GetValue<string>().ShouldBe("198.51.100.20");

        var wireguard = Outbound(XrayConfigBuilder.Build(Request(Link("wireguard/preshared-key")), tun with { ServerAddress = "2001:db8::20" }).Config!,
            XrayConfigBuilder.ProxyTag);
        wireguard["settings"]!["peers"]![0]!["endpoint"]!.GetValue<string>().ShouldEndWith("]:" + Link("wireguard/preshared-key").Port);
        wireguard["settings"]!["peers"]![0]!["endpoint"]!.GetValue<string>().ShouldStartWith("[2001:db8::20]");
        Outbound(XrayConfigBuilder.Build(Request(Link("wireguard/preshared-key")), tun).Config!, XrayConfigBuilder.ProxyTag)["settings"]!["peers"]![0]!["endpoint"]!
            .GetValue<string>().ShouldBe("198.51.100.20:" + Link("wireguard/preshared-key").Port);
    }

    [Fact]
    public void Fake_dns_answers_tun_queries_from_its_pool_and_restores_names()
    {
        var tun = new TunLink(31000, "user-1", "pass-1", "198.51.100.20");
        var server = Link("vless/reality-vision");
        var fake = ProtectionSettings.Default with { FakeDns = true };

        var config = XrayConfigBuilder.Build(Request(server, protection: fake), tun).Config.ShouldNotBeNull();

        Outbound(config, XrayConfigBuilder.DnsTag)["settings"]!["nonIPQuery"]!.GetValue<string>()
            .ShouldBe("reject", "PTR, SRV, MX, TXT — сразу отказ, а не тайм-аут DNS ОС на каждый");
        var pool = config["fakedns"]!.AsArray().ShouldHaveSingleItem()!;
        (pool["ipPool"]!.GetValue<string>(), pool["poolSize"]!.GetValue<int>()).ShouldBe(("198.18.0.0/15", 65535));
        Servers(config).ShouldBe([NcsiNames(server), "fakedns", XrayConfigBuilder.DefaultDns],
            "запросы из TUN — сначала FakeDNS, кроме имен проверки сети Windows и самого сервера; свои запросы Xray FakeDNS пропускают");
        Servers(XrayConfigBuilder.Build(Request(server with { Host = "vpn.example.com" }, protection: fake), tun).Config!)[0]
            .ShouldBe($"{XrayConfigBuilder.DefaultDns} → domain:msftncsi.com,domain:msftconnecttest.com,full:vpn.example.com",
                "сервер по имени: служба ищет его адрес и через DNS ОС в туннеле — ей нужен настоящий");
        Sniffing(config).ShouldBe((true, "http,tls,quic,fakedns", false, true),
            "имя по адресу из пула; для остальных адресов домен — только для правил");
        Outbound(config, XrayConfigBuilder.DirectTag)["settings"]!["domainStrategy"]!.GetValue<string>()
            .ShouldBe("UseIPv4v6", "имя из FakeDNS разрешает DNS ядра через туннель, а не DNS ОС; сначала IPv4 — UseIP брал бы адрес наугад");
        Outbound(XrayConfigBuilder.Build(Request(server, protection: fake with { Addresses = AddressPreference.PreferIpv6 }), tun).Config!,
            XrayConfigBuilder.DirectTag)["settings"]!["domainStrategy"]!.GetValue<string>().ShouldBe("UseIPv6v4", "по «Приоритету адресов»");

        var bare = XrayConfigBuilder.Build(Request(server, protection: fake, connection: ConnectionSettings.Default with { Sniffing = false }), tun).Config!;
        Sniffing(bare).ShouldBe((true, "fakedns", true, true), "без распознавания FakeDNS берет имя по адресу, не читая данных");

        var proxy = Build(server, ConnectionMode.Proxy, fake);
        proxy.ContainsKey("fakedns").ShouldBeFalse("в режиме прокси программы отдают имя сами");
        Servers(proxy).ShouldBe([XrayConfigBuilder.DefaultDns]);
        Sniffing(proxy).ShouldBe((true, "http,tls,quic", false, true));
        Outbound(proxy, XrayConfigBuilder.DirectTag)["settings"]!["domainStrategy"]!.GetValue<string>().ShouldBe("AsIs");

        Strategy(config).ShouldBe("AsIs", "правилам адрес имени не нужен — имя разрешает сервер, без запроса к DNS через туннель");
        Strategy(XrayConfigBuilder.Build(Request(server, protection: fake with { AllowLan = true }), tun).Config!)
            .ShouldBe("IPIfNonMatch", "локальная сеть разрешена — имя с адресом в ней идет напрямую");
        var subnet = new RoutingSettings(RoutingMode.Split, [], [new DomainRule("203.0.113.0/24", RuleAction.Direct)]);
        Strategy(XrayConfigBuilder.Build(Request(server, protection: fake, routing: subnet), tun).Config!)
            .ShouldBe("IPIfNonMatch", "правило для подсети сравнивает и адреса имен");
        var names = new RoutingSettings(RoutingMode.Split, [], [new DomainRule("example.com", RuleAction.Direct)]);
        Strategy(XrayConfigBuilder.Build(Request(server, protection: fake, routing: names), tun).Config!).ShouldBe("AsIs");
        Strategy(proxy).ShouldBe("IPIfNonMatch");

        var plain = XrayConfigBuilder.Build(Request(server), tun).Config!;
        Strategy(plain).ShouldBe("IPIfNonMatch");
        plain.ContainsKey("fakedns").ShouldBeFalse();
        Servers(plain).ShouldBe([XrayConfigBuilder.DefaultDns]);
        Sniffing(plain).ShouldBe((true, "http,tls,quic", false, true));
    }

    [Fact]
    public void Fake_dns_with_split_programs_gets_a_bypass_inbound_straight_to_direct()
    {
        var server = Link("vless/reality-vision");
        var fake = ProtectionSettings.Default with { FakeDns = true };
        var split = new RoutingSettings(RoutingMode.Split, [new BypassApp("/opt/game/game", "Игра")], []);
        var request = Request(server, protection: fake, routing: split);

        XrayConfigBuilder.NeedsBypassInbound(request).ShouldBeTrue();
        XrayConfigBuilder.NeedsBypassInbound(request with { Protection = ProtectionSettings.Default }).ShouldBeFalse("без FakeDNS программы получают настоящие адреса");
        XrayConfigBuilder.NeedsBypassInbound(request with { Routing = RoutingSettings.Default }).ShouldBeFalse("«Весь трафик» — обхода нет");
        XrayConfigBuilder.NeedsBypassInbound(request with { Mode = ConnectionMode.Proxy }).ShouldBeFalse();

        var config = XrayConfigBuilder.Build(request, new TunLink(31000, "user-1", "pass-1", "198.51.100.20", BypassPort: 31001)).Config!;

        var bypass = config["inbounds"]!.AsArray().Single(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.BypassTag)!;
        (bypass["listen"]!.GetValue<string>(), bypass["port"]!.GetValue<int>(), bypass["settings"]!["auth"]!.GetValue<string>())
            .ShouldBe(("127.0.0.1", 31001, "password"));
        var account = bypass["settings"]!["accounts"]![0]!;
        (account["user"]!.GetValue<string>(), account["pass"]!.GetValue<string>()).ShouldBe(("user-1", "pass-1"));
        bypass["sniffing"]!["destOverride"]!.AsArray().Select(d => d!.GetValue<string>()).ShouldContain("fakedns", "имя по адресу из пула");
        var first = config["routing"]!["rules"]![0]!;
        (first["inboundTag"]![0]!.GetValue<string>(), first["outboundTag"]!.GetValue<string>())
            .ShouldBe((XrayConfigBuilder.BypassTag, XrayConfigBuilder.DirectTag), "первым правилом: программа мимо туннеля — напрямую");

        XrayConfigBuilder.Build(request, new TunLink(31000, "user-1", "pass-1", "198.51.100.20")).Config!["inbounds"]!.AsArray()
            .ShouldNotContain(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.BypassTag, "нет порта — нет входа");
        XrayConfigBuilder.Build(request with { Protection = ProtectionSettings.Default }, new TunLink(31000, "user-1", "pass-1", "198.51.100.20", BypassPort: 31001))
            .Config!["inbounds"]!.AsArray().ShouldNotContain(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.BypassTag, "без FakeDNS — не нужен");
    }

    private static JsonObject Build(
        ServerDefinition server,
        ConnectionMode mode = ConnectionMode.Vpn,
        ProtectionSettings? protection = null,
        ConnectionSettings? connection = null,
        RoutingSettings? routing = null)
    {
        var result = XrayConfigBuilder.Build(Request(server, mode, protection, connection, routing));
        result.Rejection.ShouldBeNull();
        return result.Config.ShouldNotBeNull();
    }

    private static CoreRequest Request(
        ServerDefinition server,
        ConnectionMode mode = ConnectionMode.Vpn,
        ProtectionSettings? protection = null,
        ConnectionSettings? connection = null,
        RoutingSettings? routing = null) =>
        new(server, mode, protection ?? ProtectionSettings.Default, connection ?? ConnectionSettings.Default, routing ?? RoutingSettings.Default);

    private static JsonNode Outbound(JsonObject config, string tag) =>
        config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == tag)!;

    private static string Strategy(JsonObject config) => config["routing"]!["domainStrategy"]!.GetValue<string>();

    /// <summary>Распознавание первого входа; destOverride — через запятую.</summary>
    private static (bool Enabled, string DestOverride, bool MetadataOnly, bool RouteOnly) Sniffing(JsonObject config)
    {
        var sniffing = config["inbounds"]![0]!["sniffing"]!;
        return (sniffing["enabled"]!.GetValue<bool>(), string.Join(',', sniffing["destOverride"]!.AsArray().Select(d => d!.GetValue<string>())),
            sniffing["metadataOnly"]?.GetValue<bool>() ?? false, sniffing["routeOnly"]!.GetValue<bool>());
    }

    /// <summary>Серверы DNS: адрес, а у сервера для своих имен — «адрес → имена» (и только для них: skipFallback).</summary>
    private static List<string> Servers(JsonObject config) =>
    [
        .. config["dns"]!["servers"]!.AsArray().Select(s => s is JsonObject named
            ? $"{named["address"]!.GetValue<string>()} → {string.Join(",", named["domains"]!.AsArray().Select(d => d!.GetValue<string>()))}"
                + (named["skipFallback"]!.GetValue<bool>() ? string.Empty : " и для остальных")
            : s!.GetValue<string>()),
    ];

    private static List<(string Condition, string Outbound)> Rules(JsonObject config) =>
    [
        .. config["routing"]!["rules"]!.AsArray().Select(r => (
            r!["ip"] is JsonArray ip ? "ip:" + ip[0]!.GetValue<string>() : "domain:" + r["domain"]![0]!.GetValue<string>(),
            r["outboundTag"]!.GetValue<string>())),
    ];

    /// <summary>Настоящий DNS раньше FakeDNS: имена проверки сети Windows и имя сервера, если он задан именем.</summary>
    private static string NcsiNames(ServerDefinition server) =>
        $"{XrayConfigBuilder.DefaultDns} → domain:msftncsi.com,domain:msftconnecttest.com"
        + (System.Net.IPAddress.TryParse(server.Host.Trim('[', ']'), out _) ? string.Empty : ",full:" + server.Host);

    private static ServerDefinition Link(string name) =>
        LinkParser.Parse(File.ReadAllText(Path.Combine(Links, name + ".txt")).Trim()).Server.ShouldNotBeNull(name);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория");
    }
}
