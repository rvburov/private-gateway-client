using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Входной SOCKS5 — единственный физический адрес цепочки до выбранного VLESS.</summary>
public sealed class EntryProxyCoreTests
{
    private static readonly ServerDefinition Entry = new("Входной прокси", "", ProxyProtocol.Socks5, "proxy.example.net", 12324,
        TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty,
        new ServerCredentials(Username: "example-user", Password: "example-password"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vpn_dials_only_the_pinned_entry_proxy_and_preserves_the_vless_destination(bool fragment)
    {
        var target = XrayConfigTests.Link("vless/reality-vision");
        var request = Request(target, fragment);
        request.PhysicalServer.ShouldBe(Entry);
        var config = XrayConfigBuilder.Build(request, new TunLink(31000, "example-tun-password", "198.51.100.20", Outside: "en0"))
            .Config.ShouldNotBeNull();

        var vless = Outbound(config, XrayConfigBuilder.ProxyTag);
        var destination = vless["settings"]!["vnext"]![0]!;
        destination["address"]!.GetValue<string>().ShouldBe(target.Host, "SOCKS CONNECT получает адрес VPS, а не адрес входного прокси");
        destination["port"]!.GetValue<int>().ShouldBe(target.Port);
        vless["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>()
            .ShouldBe(fragment ? XrayConfigBuilder.FragmentTag : XrayConfigBuilder.EntryProxyTag);

        var entry = Outbound(config, XrayConfigBuilder.EntryProxyTag);
        entry["protocol"]!.GetValue<string>().ShouldBe("socks");
        var server = entry["settings"]!["servers"]![0]!;
        server["address"]!.GetValue<string>().ShouldBe("198.51.100.20", "TUN и защита разрешают только проверенный IP прокси");
        server["port"]!.GetValue<int>().ShouldBe(Entry.Port);
        server["users"]![0]!["user"]!.GetValue<string>().ShouldBe(Entry.Credentials.Username);
        server["users"]![0]!["pass"]!.GetValue<string>().ShouldBe(Entry.Credentials.Password);
        entry["streamSettings"]!["sockopt"]!["interface"]!.GetValue<string>().ShouldBe("en0");
        vless["streamSettings"]!["sockopt"]!["interface"].ShouldBeNull("физический сокет создает SOCKS outbound");

        if (fragment)
        {
            Outbound(config, XrayConfigBuilder.FragmentTag)["streamSettings"]!["sockopt"]!["dialerProxy"]!
                .GetValue<string>().ShouldBe(XrayConfigBuilder.EntryProxyTag);
        }
    }

    [Fact]
    public void Latency_probe_uses_its_corresponding_entry_proxy_without_changing_other_profiles()
    {
        var target = XrayConfigTests.Link("vless/reality-vision");
        var probe = XrayConfigBuilder.BuildProbe([target, target], [30001, 30002],
            ConnectionSettings.Default with { TlsFragment = true }, entryProxies: [Entry, null]);
        probe.Rejections.ShouldBe([null, null]);
        var config = probe.Config.ShouldNotBeNull();
        Outbound(config, "probe-out-0")["streamSettings"]!["sockopt"]!["dialerProxy"]!
            .GetValue<string>().ShouldBe("fragment-0");
        Outbound(config, "fragment-0")["streamSettings"]!["sockopt"]!["dialerProxy"]!
            .GetValue<string>().ShouldBe("entry-proxy-0");
        Outbound(config, "entry-proxy-0")["settings"]!["servers"]![0]!["address"]!
            .GetValue<string>().ShouldBe(Entry.Host);
        Outbound(config, "probe-out-1")["streamSettings"]!["sockopt"]!["dialerProxy"]!
            .GetValue<string>().ShouldBe(XrayConfigBuilder.FragmentTag);
        Outbound(config, XrayConfigBuilder.FragmentTag)["protocol"]!.GetValue<string>().ShouldBe("freedom");
    }

    [Fact]
    public void H3_and_non_socks_entries_fail_closed()
    {
        var h3 = XrayConfigTests.Link("vless/tls-xhttp") with
        {
            Security = XrayConfigTests.Link("vless/tls-xhttp").Security with { Alpn = ["h3"] },
        };
        XrayConfigBuilder.Build(Request(h3)).Rejection.ShouldBe(CoreRejection.UnsupportedEntryProxy);
        XrayConfigBuilder.BuildProbe([h3], [30001], ConnectionSettings.Default, entryProxies: [Entry])
            .Rejections.ShouldBe([CoreRejection.UnsupportedEntryProxy]);

        var http = Entry with { Protocol = ProxyProtocol.Http };
        XrayConfigBuilder.Build(Request(XrayConfigTests.Link("vless/reality-vision")) with { EntryProxy = http })
            .Rejection.ShouldBe(CoreRejection.UnsupportedEntryProxy);
    }

    [Fact]
    public void Own_proxy_listener_cannot_be_used_as_the_entry_proxy()
    {
        var request = Request(XrayConfigTests.Link("vless/reality-vision")) with
        {
            Mode = ConnectionMode.Proxy,
            Connection = ConnectionSettings.Default with { ProxyPort = 2080 },
            EntryProxy = Entry with { Host = "127.0.0.1", Port = 2080 },
        };
        XrayConfigBuilder.Build(request).Rejection.ShouldBe(CoreRejection.InvalidEndpoint);
    }

    private static CoreRequest Request(ServerDefinition target, bool fragment = false) => new(target, ConnectionMode.Vpn,
        ProtectionSettings.Default, ConnectionSettings.Default with { TlsFragment = fragment }, RoutingSettings.Default, EntryProxy: Entry);

    private static System.Text.Json.Nodes.JsonNode Outbound(System.Text.Json.Nodes.JsonObject config, string tag) =>
        config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == tag)!;
}
