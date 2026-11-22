using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Дополнительный канал XHTTP — точные разрешения транспорта, без открытия соседних адресов и портов.</summary>
public sealed class GatewayTransportEndpointTests
{
    private static readonly IPAddress Primary = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress Download = IPAddress.Parse("203.0.113.40");

    [Fact]
    public void Valid_unicode_header_remains_bounded_when_download_addresses_are_prepared_and_pinned()
    {
        var secret = new string('я', 9000);
        var core = Request("{\"headers\":{\"X-Private-Key\":\"" + secret
            + "\"},\"downloadSettings\":{\"address\":\"download.example\",\"port\":8444,\"network\":\"xhttp\",\"security\":\"tls\"}}");
        var preparedText = TransportParameterRules.ExtraForServer(core.Server).ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetByteCount(preparedText).ShouldBeLessThan(TransportParameterRules.MaxExtraBytes);
        TransportParameterRules.TryXhttpExtra(preparedText, out _).ShouldBeTrue();

        var pinned = Gateway.WithTransportEndpoints(core, [Primary], Resolved("download.example", 8444, Download));
        var actual = pinned.ResolvedTransportServer.ShouldNotBeNull();
        TransportParameterRules.SensitiveExtraValues(actual).ShouldBe([secret]);
        TransportParameterRules.TryXhttpExtra(actual.Transport.ExtraJson, out _).ShouldBeTrue();
        JsonNode.Parse(actual.Transport.ExtraJson!)!["downloadSettings"]!["address"]!.GetValue<string>().ShouldBe(Download.ToString());
    }

    [Fact]
    public void Distinct_download_is_pinned_without_changing_profile_identity_or_explicit_names()
    {
        var core = Request("""
            {"downloadSettings":{"address":"download.example","port":8444,"network":"xhttp","security":"tls",
            "tlsSettings":{"serverName":"certificate.example"},"xhttpSettings":{"host":"http.example","path":"/download"}}}
            """);
        var prepared = Gateway.WithTransportEndpoints(core, [Primary], Resolved("download.example", 8444, Download));

        prepared.Server.ShouldBeSameAs(core.Server);
        prepared.TransportEndpoints.ShouldBe([new KillSwitchEndpoint(Primary, 443), new KillSwitchEndpoint(Download, 8444)]);
        var built = XrayConfigBuilder.Build(prepared);
        built.Config.ShouldNotBeNull();
        var outbound = built.Config!["outbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == XrayConfigBuilder.ProxyTag)!;
        var download = outbound["streamSettings"]!["xhttpSettings"]!["extra"]!["downloadSettings"]!;
        download["address"]!.GetValue<string>().ShouldBe(Download.ToString());
        download["tlsSettings"]!["serverName"]!.GetValue<string>().ShouldBe("certificate.example");
        download["xhttpSettings"]!["host"]!.GetValue<string>().ShouldBe("http.example");
        core.Server.Transport.ExtraJson!.ShouldContain("download.example");
    }

    [Fact]
    public void Implicit_tls_and_http_names_survive_replacing_the_download_dns_name()
    {
        var core = Request("""{"downloadSettings":{"address":"download.example","port":8444,"network":"xhttp","security":"tls"}}""");
        var prepared = Gateway.WithTransportEndpoints(core, [Primary], Resolved("download.example", 8444, Download));
        var download = JsonNode.Parse(prepared.ResolvedTransportServer!.Transport.ExtraJson!)!["downloadSettings"]!;

        download["tlsSettings"]!["serverName"]!.GetValue<string>().ShouldBe("download.example");
        download["xhttpSettings"]!["host"]!.GetValue<string>().ShouldBe("download.example:8444");
        download["address"]!.GetValue<string>().ShouldBe(Download.ToString());
    }

    [Fact]
    public void Nested_download_inherits_the_original_parent_before_its_address_is_pinned()
    {
        var core = Request("""
            {"downloadSettings":{"address":"download.example","port":8444,"network":"xhttp",
            "xhttpSettings":{"extra":{"downloadSettings":{"port":8445,"network":"xhttp"}}}}}
            """);
        var resolved = Resolved("download.example", 8444, Download);
        resolved.Add(new TransportEndpoint("download.example", 8445), Download);
        var prepared = Gateway.WithTransportEndpoints(core, [Primary], resolved);
        var download = JsonNode.Parse(prepared.ResolvedTransportServer!.Transport.ExtraJson!)!["downloadSettings"]!;
        var nested = download["xhttpSettings"]!["extra"]!["downloadSettings"]!;

        nested["address"]!.GetValue<string>().ShouldBe(Download.ToString());
        nested["port"]!.GetValue<int>().ShouldBe(8445);
        prepared.TransportEndpoints!.Count.ShouldBe(3);
    }

    [Fact]
    public void An_unresolved_or_fake_dns_download_does_not_produce_a_partial_policy()
    {
        var core = Request("""{"downloadSettings":{"address":"download.example","port":8444}}""");
        var missing = Should.Throw<ArgumentException>(() => Gateway.WithTransportEndpoints(core, [Primary], new Dictionary<TransportEndpoint, IPAddress>()));
        missing.Message.ShouldNotContain("download.example");
        Should.Throw<ArgumentException>(() => Gateway.WithTransportEndpoints(core, [Primary],
            Resolved("download.example", 8444, IPAddress.Parse("198.18.0.2"))));
        core.TransportEndpoints.ShouldBeNull();
        core.ResolvedTransportServer.ShouldBeNull();
    }

    [Fact]
    public void A_policy_over_the_firewall_limit_is_refused_instead_of_dropping_an_endpoint()
    {
        var core = Request("""{"downloadSettings":{"address":"download.example","port":8444}}""");
        var primary = Enumerable.Range(1, KillSwitchPolicy.MaxServers).Select(index => IPAddress.Parse($"198.51.100.{index}")).ToArray();

        Should.Throw<ArgumentException>(() => Gateway.WithTransportEndpoints(core, primary, Resolved("download.example", 8444, Download)));
    }

    [Fact]
    public void Tun_fallback_opens_only_declared_address_port_pairs()
    {
        var prepared = Gateway.WithTransportEndpoints(Request("""{"downloadSettings":{"address":"download.example","port":8444}}"""),
            [Primary], Resolved("download.example", 8444, Download));
        var request = new TunRequest(new TunLink(2080, "test-only", Primary.ToString()), [Primary], 443,
            ProtectionSettings.Default, ConnectionSettings.Default, new TunIpv6(false, false), TransportEndpoints: prepared.TransportEndpoints);
        var rules = SingBoxConfigBuilder.BuildTun(request)["route"]!["rules"]!.AsArray();

        bool Direct(IPAddress address, int port) => rules.Any(rule => rule?["outbound"]?.GetValue<string>() == SingBoxConfigBuilder.ServerDirectTag
            && rule["port"]!.GetValue<int>() == port && rule["ip_cidr"]!.AsArray().Any(ip => ip!.GetValue<string>() == address + "/32"));

        Direct(Primary, 443).ShouldBeTrue();
        Direct(Download, 8444).ShouldBeTrue();
        Direct(Primary, 8444).ShouldBeFalse("другой порт основного IP не становится прямым выходом");
        Direct(Download, 443).ShouldBeFalse("не образуем произведение всех адресов и портов");
        Direct(IPAddress.Parse("203.0.113.41"), 8444).ShouldBeFalse();
    }

    [Fact]
    public void Saved_block_distinguishes_primary_addresses_from_a_download_on_the_same_port()
    {
        var directory = Directory.CreateTempSubdirectory("pg-transport-policy-").FullName;
        try
        {
            var path = Path.Combine(directory, "block.json");
            var policy = new KillSwitchPolicy(null, [new KillSwitchEndpoint(Primary, 443), new KillSwitchEndpoint(Download, 443)]);
            BootBlock.Save(path, new HeldBlock(policy, "entry.example", Boot: true, Strict: true, PrimaryAddresses: [Primary]));
            var restored = BootBlock.Read(path);

            restored.ShouldNotBeNull();
            restored.PrimaryAddresses.ShouldBe([Primary], "дополнительный IP нельзя вернуть как адрес основного имени при перезапуске");
            restored.Policy.Servers.ShouldBe(policy.Servers);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<TransportEndpoint, IPAddress> Resolved(string host, int port, IPAddress address) =>
        new() { [new TransportEndpoint(host, port)] = address };

    private static CoreRequest Request(string extra) => new(
        new ServerDefinition("test", "US", ProxyProtocol.Vless, "entry.example", 443,
            new TransportOptions(TransportKind.Xhttp, Path: "/", ExtraJson: extra),
            new SecurityOptions(TransportSecurity.Tls, Sni: "entry.example"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: "11111111-1111-4111-8111-111111111111")),
        ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
}
