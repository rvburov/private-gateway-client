using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class TransportCompatibilityTests
{
    private static readonly string[] HeaderSecrets = ["demo-secret", "Bearer demo-bearer", "demo-cookie"];
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST.")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]")]
    public void Local_profiles_do_not_bind_probe_sockets_to_the_physical_interface(string host)
    {
        var server = XrayConfigTests.Link("vless/reality-vision") with { Host = host };
        var probe = XrayConfigBuilder.BuildProbe([server], [15001], ConnectionSettings.Default, outside: "en0");
        probe.Config.ShouldNotBeNull().ToJsonString().Contains("en0", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void Extra_header_secrets_include_separate_download_but_not_arbitrary_fields()
    {
        var server = XrayConfigTests.Link("vless/xhttp-extra");
        var extra = """{"headers":{"X-Private-Key":"demo-secret","Authorization":"Bearer demo-bearer"},"xPaddingKey":"public-padding","downloadSettings":{"network":"xhttp","xhttpSettings":{"extra":{"headers":{"Cookie":"demo-cookie"}}}}}""";
        server = server with { Transport = server.Transport with { ExtraJson = extra } };
        TransportParameterRules.SensitiveExtraValues(server).Order().ShouldBe(HeaderSecrets.Order());
        TransportParameterRules.SensitiveExtraValues(server with { Transport = server.Transport with { ExtraJson = "{\"headers\":{\"X-Key\":\"untrusted\"},\"sockopt\":{}}" } }).ShouldBeEmpty();

        var unicode = "{\"headers\":{\"X-Private-Key\":\"" + new string('я', 9000) + "\"}}";
        TransportParameterRules.TryXhttpExtra(unicode, out var normalized).ShouldBeTrue();
        TransportParameterRules.TryXhttpExtra(normalized, out _).ShouldBeTrue("нормализация не раздувает допустимый UTF-8 за предел extra");
    }

    [Fact]
    public void Xhttp_extra_survives_contract_and_reaches_its_own_stream()
    {
        var imported = XrayConfigTests.Link("vless/xhttp-extra");
        var server = imported.ToContract("profile").ToCore();
        server.Transport.ExtraJson.ShouldBe(imported.Transport.ExtraJson);
        server.Security.Alpn.ShouldBe(["h3"]);

        var stream = Proxy(Build(server))["streamSettings"]!;
        var extra = stream["xhttpSettings"]!["extra"]!;
        extra["noGRPCHeader"]!.GetValue<bool>().ShouldBeTrue();
        extra["xmux"]!["maxConcurrency"]!.GetValue<string>().ShouldBe("4-8");
        extra["downloadSettings"]!["address"]!.GetValue<string>().ShouldBe("download.example.com");
        extra["downloadSettings"]!["tlsSettings"]!["alpn"]![0]!.GetValue<string>().ShouldBe("h3");
        TransportParameterRules.DownloadEndpoints(server).ShouldBe([new TransportEndpoint("download.example.com", 443)]);
    }

    [Fact]
    public void Vless_encryption_survives_contract_and_is_not_downgraded_to_none()
    {
        var imported = XrayConfigTests.Link("vless/encryption-mlkem");
        var server = imported.ToContract("encrypted").ToCore();

        server.Options.Encryption.ShouldBe(imported.Options.Encryption);
        XrayConfigBuilder.Unencrypted(server, server.Host).ShouldBeFalse();
        Proxy(Build(server))["settings"]!["vnext"]![0]!["users"]![0]!["encryption"]!.GetValue<string>()
            .ShouldBe(imported.Options.Encryption);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mlkem768x25519plus.native.1rtt.bad-key")]
    [InlineData("mlkem768x25519plus.native.1rtt")]
    public void Unsupported_encryption_is_rejected_at_the_service_boundary(string encryption)
    {
        var server = XrayConfigTests.Link("vless/reality-vision");
        var endpoint = server.ToContract("invalid");
        endpoint.Vless.Encryption = encryption;

        Should.Throw<ArgumentException>(() => endpoint.ToCore());
        var forged = server with { Options = server.Options with { Encryption = encryption } };
        XrayConfigBuilder.Build(Request(forged)).Rejection.ShouldBe(CoreRejection.UnsupportedTransport);
    }

    [Theory]
    [InlineData("{\"downloadSettings\":{\"sockopt\":{\"dialerProxy\":\"direct\"}}}")]
    [InlineData("{\"downloadSettings\":{\"tlsSettings\":{\"allowInsecure\":true}}}")]
    [InlineData("{\"outbounds\":[]}")]
    [InlineData("{\"downloadSettings\":{\"port\":\"443\"}}")]
    public void Untrusted_extra_cannot_override_service_security(string extra)
    {
        var server = XrayConfigTests.Link("vless/xhttp-extra");
        var endpoint = server.ToContract("invalid");
        endpoint.Transport.ExtraJson = extra;

        Should.Throw<ArgumentException>(() => endpoint.ToCore());
        var forged = server with { Transport = server.Transport with { ExtraJson = extra } };
        XrayConfigBuilder.Build(Request(forged)).Rejection.ShouldBe(CoreRejection.UnsupportedTransport);
    }

    [Fact]
    public void Oversize_extra_is_rejected_without_retaining_its_contents()
    {
        var extra = "{\"headers\":{\"X-Padding\":\"" + new string('a', TransportParameterRules.MaxExtraBytes) + "\"}}";
        TransportParameterRules.TryXhttpExtra(extra, out var normalized).ShouldBeFalse();
        normalized.ShouldBeNull();
        var link = "vless://11111111-2222-3333-4444-555555555555@192.0.2.1:443?type=xhttp&extra=" + Uri.EscapeDataString(extra);
        LinkParser.Parse(link).Error.ShouldBe(ImportErrors.UnsupportedParameter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_chain_keeps_profile_fragment_and_download_on_the_protected_upstream(bool fragment)
    {
        var server = XrayConfigTests.Link("vless/xhttp-extra");
        var tcpExtra = JsonNode.Parse(server.Transport.ExtraJson!)!;
        tcpExtra["downloadSettings"]!["tlsSettings"]!["alpn"]![0] = "h2";
        server = server with { Security = server.Security with { Alpn = ["h2"] }, Transport = server.Transport with { ExtraJson = tcpExtra.ToJsonString() } };
        var result = XrayConfigBuilder.BuildProbe([server], [15001], ConnectionSettings.Default with { TlsFragment = fragment },
            account: new SocksAccount("probe", "probe-password"), outside: "en0",
            upstream: new XrayProbeUpstream(15002, new SocksAccount("vpn", "vpn-password")));
        var config = result.Config.ShouldNotBeNull();
        var profile = Outbound(config, "probe-out-0");
        var expected = fragment ? XrayConfigBuilder.FragmentTag : "probe-upstream";
        profile["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>().ShouldBe(expected);
        profile["streamSettings"]!["xhttpSettings"]!["extra"]!["downloadSettings"]!["sockopt"]!["dialerProxy"]!
            .GetValue<string>().ShouldBe(expected);
        if (fragment)
        {
            Outbound(config, XrayConfigBuilder.FragmentTag)["streamSettings"]!["sockopt"]!["dialerProxy"]!.GetValue<string>()
                .ShouldBe("probe-upstream");
        }

        var upstream = Outbound(config, "probe-upstream");
        upstream["settings"]!["servers"]![0]!["port"]!.GetValue<int>().ShouldBe(15002);
        upstream["settings"]!["servers"]![0]!["users"]![0]!["pass"]!.GetValue<string>().ShouldBe("vpn-password");
        config.ToJsonString().Contains("en0", StringComparison.Ordinal).ShouldBeFalse("защищенный замер не выходит напрямую на физический интерфейс");
    }

    [Fact]
    public void Http3_is_distinguished_from_a_tcp_probe_and_is_not_forced_to_http2()
    {
        var server = XrayConfigTests.Link("vless/xhttp-extra");
        TransportParameterRules.RequiresUdpProbe(server).ShouldBeTrue();
        var protectedProbe = XrayConfigBuilder.BuildProbe([server], [15001], ConnectionSettings.Default,
            upstream: new XrayProbeUpstream(15002, new SocksAccount("vpn", "vpn-password")));
        protectedProbe.Config.ShouldBeNull();
        protectedProbe.Rejections.ShouldBe([CoreRejection.UnsupportedTransport]);
        var reality = XrayConfigTests.Link("vless/reality-vision");
        TransportParameterRules.RequiresUdpProbe(reality with { Security = reality.Security with { Alpn = ["h3"] } }).ShouldBeFalse();
    }

    [Fact]
    public void Download_without_address_inherits_the_main_endpoint_and_is_bound_outside_tun()
    {
        var server = XrayConfigTests.Link("vless/xhttp-extra");
        server = server with { Transport = server.Transport with { ExtraJson = "{\"downloadSettings\":{\"network\":\"xhttp\"}}" } };
        var result = XrayConfigBuilder.BuildProbe([server], [15001], ConnectionSettings.Default, outside: "en0");
        var download = Outbound(result.Config!, "probe-out-0")["streamSettings"]!["xhttpSettings"]!["extra"]!["downloadSettings"]!;

        download["address"]!.GetValue<string>().ShouldBe(server.Host);
        download["port"]!.GetValue<int>().ShouldBe(server.Port);
        download["sockopt"]!["interface"]!.GetValue<string>().ShouldBe("en0");
    }

    private static CoreRequest Request(ServerDefinition server) =>
        new(server, ConnectionMode.Proxy, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);

    private static JsonObject Build(ServerDefinition server) => XrayConfigBuilder.Build(Request(server)).Config.ShouldNotBeNull();

    private static JsonNode Proxy(JsonObject config) => Outbound(config, XrayConfigBuilder.ProxyTag);

    private static JsonNode Outbound(JsonObject config, string tag) =>
        config["outbounds"]!.AsArray().Single(node => node!["tag"]!.GetValue<string>() == tag)!;
}
