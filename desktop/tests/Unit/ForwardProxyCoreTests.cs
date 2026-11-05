using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network;
using PrivateGateway.Client.Network.Xray;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Внешний прокси: отдельный endpoint, явная авторизация и совместимое описание для службы.</summary>
public sealed class ForwardProxyCoreTests
{
    private const string User = "not-a-real-proxy-user";
    private const string Password = "not-a-real-proxy-password";

    [Theory]
    [InlineData(ProxyProtocol.Http, TransportSecurity.None, false, "http")]
    [InlineData(ProxyProtocol.Http, TransportSecurity.None, true, "http")]
    [InlineData(ProxyProtocol.Http, TransportSecurity.Tls, false, "http")]
    [InlineData(ProxyProtocol.Http, TransportSecurity.Tls, true, "http")]
    [InlineData(ProxyProtocol.Socks5, TransportSecurity.None, false, "socks")]
    [InlineData(ProxyProtocol.Socks5, TransportSecurity.None, true, "socks")]
    public void External_endpoint_and_credentials_reach_the_core_without_reusing_local_port_or_mux(
        ProxyProtocol protocol, TransportSecurity security, bool authenticated, string outboundProtocol)
    {
        var definition = Definition(protocol, security, authenticated);
        var request = Request(definition);

        var config = XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
        var outbound = config["outbounds"]![0]!;
        outbound["protocol"]!.GetValue<string>().ShouldBe(outboundProtocol);
        var endpoint = outbound["settings"]!["servers"]!.AsArray().ShouldHaveSingleItem()!;
        endpoint["address"]!.GetValue<string>().ShouldBe("proxy.example.com");
        endpoint["port"]!.GetValue<int>().ShouldBe(8443);
        config["inbounds"]![0]!["port"]!.GetValue<int>().ShouldBe(2080, "локальный порт остается самостоятельной настройкой");
        outbound["mux"].ShouldBeNull("обычные HTTP/SOCKS-прокси не понимают Xray Mux");
        outbound["streamSettings"]!["security"]!.GetValue<string>().ShouldBe(security == TransportSecurity.Tls ? "tls" : "none");
        if (authenticated)
        {
            var account = endpoint["users"]!.AsArray().ShouldHaveSingleItem()!;
            account["user"]!.GetValue<string>().ShouldBe(User);
            account["pass"]!.GetValue<string>().ShouldBe(Password);
        }
        else
        {
            endpoint["users"].ShouldBeNull();
        }

        var contract = definition.ToContract("forward-fixture");
        contract.SettingsCase.ShouldBe(protocol == ProxyProtocol.Http ? V1.ServerEndpoint.SettingsOneofCase.Http : V1.ServerEndpoint.SettingsOneofCase.Socks5);
        var roundTrip = contract.ToCore();
        roundTrip.ShouldBe(definition, "IPC сохраняет endpoint, TLS и учетные данные выбранного протокола");
        request.Connection.Mux.ShouldBeTrue("сохраненная техническая настройка для VPN не переписывается");
        var probe = XrayConfigBuilder.BuildProbe([definition], [2081], request.Connection).Config.ShouldNotBeNull();
        probe["outbounds"]![0]!["protocol"]!.GetValue<string>().ShouldBe(outboundProtocol);
        probe["outbounds"]![0]!["mux"].ShouldBeNull();
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, TransportKind.WebSocket, TransportSecurity.None)]
    [InlineData(ProxyProtocol.Http, TransportKind.Tcp, TransportSecurity.Reality)]
    [InlineData(ProxyProtocol.Socks5, TransportKind.Grpc, TransportSecurity.None)]
    [InlineData(ProxyProtocol.Socks5, TransportKind.Tcp, TransportSecurity.Tls)]
    public void Unsupported_forward_transport_is_rejected_by_builder_and_ipc(
        ProxyProtocol protocol, TransportKind transport, TransportSecurity security)
    {
        var definition = Definition(protocol, security) with { Transport = new TransportOptions(transport) };

        XrayConfigBuilder.Build(Request(definition)).Rejection.ShouldBe(CoreRejection.UnsupportedTransport);
        Should.Throw<ArgumentException>(() => definition.ToContract("forward-fixture").ToCore());
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, null, Password)]
    [InlineData(ProxyProtocol.Http, User, null)]
    [InlineData(ProxyProtocol.Http, "invalid:user", Password)]
    [InlineData(ProxyProtocol.Http, "invalid\r\nuser", Password)]
    [InlineData(ProxyProtocol.Socks5, null, Password)]
    [InlineData(ProxyProtocol.Socks5, User, "invalid\npassword")]
    public void Malformed_credentials_cannot_become_anonymous_or_enter_the_core(ProxyProtocol protocol, string? user, string? password)
    {
        var credentials = new ServerCredentials(Username: user, Password: password);
        var definition = Definition(protocol) with { Credentials = credentials };

        ForwardProxyRules.AcceptsCredentials(protocol, credentials).ShouldBeFalse();
        XrayConfigBuilder.Build(Request(definition)).Rejection.ShouldBe(CoreRejection.InvalidCredentials);
        var error = Should.Throw<ArgumentException>(() => definition.ToContract("forward-fixture").ToCore());
        error.Message.ShouldNotContain(Password);
        error.Message.ShouldNotContain(User);
    }

    [Fact]
    public void Socks_credentials_use_utf8_byte_limits_and_basic_auth_only_reserves_colon_in_the_username()
    {
        var shortText = new string('я', 127);
        var longText = new string('я', 128);
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, new ServerCredentials(Username: shortText, Password: shortText)).ShouldBeTrue();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, new ServerCredentials(Username: longText, Password: Password)).ShouldBeFalse();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, new ServerCredentials(Username: User, Password: longText)).ShouldBeFalse();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, new ServerCredentials(Username: "proxy:user", Password: "pass:word")).ShouldBeTrue();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Http, new ServerCredentials(Username: User, Password: "pass:word")).ShouldBeTrue();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Http, new ServerCredentials(Username: "proxy:user", Password: Password)).ShouldBeFalse();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Http, new ServerCredentials(Username: new string('u', 4097), Password: Password)).ShouldBeFalse();
        ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Http, new ServerCredentials(Username: User, Password: new string('p', 4097))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://proxy.example.com")]
    [InlineData("proxy.example.com:8443")]
    [InlineData("user@proxy.example.com")]
    [InlineData("proxy.example.com/path")]
    [InlineData("proxy.example.com\n")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Host_field_cannot_smuggle_an_url_credentials_path_or_an_unspecified_address(string host)
    {
        var definition = Definition(ProxyProtocol.Http) with { Host = host };

        ForwardProxyRules.IsValidEndpoint(host, definition.Port).ShouldBeFalse();
        XrayConfigBuilder.Build(Request(definition)).Rejection.ShouldBe(CoreRejection.InvalidEndpoint);
        Should.Throw<ArgumentException>(() => definition.ToContract("forward-fixture").ToCore());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void External_port_has_its_own_valid_range(int port)
    {
        var definition = Definition(ProxyProtocol.Socks5) with { Port = port };

        XrayConfigBuilder.Build(Request(definition)).Rejection.ShouldBe(CoreRejection.InvalidEndpoint);
        Should.Throw<ArgumentException>(() => definition.ToContract("forward-fixture").ToCore());
        ForwardProxyRules.IsValidEndpoint("2001:db8::1", 1080).ShouldBeTrue();
        ForwardProxyRules.IsValidEndpoint("198.51.100.10", 3128).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ProxyProtocol.Http, "127.0.0.1")]
    [InlineData(ProxyProtocol.Socks5, "127.0.0.1")]
    [InlineData(ProxyProtocol.Http, "127.12.0.7")]
    [InlineData(ProxyProtocol.Http, "::1")]
    [InlineData(ProxyProtocol.Socks5, "::ffff:127.0.0.1")]
    [InlineData(ProxyProtocol.Http, "localhost.")]
    [InlineData(ProxyProtocol.Socks5, "LOCALHOST")]
    public void Forward_proxy_cannot_point_to_its_own_listener_but_same_remote_port_is_valid(ProxyProtocol protocol, string host)
    {
        var definition = Definition(protocol) with { Host = host, Port = 2080 };

        ForwardProxyRules.IsLocalLoop(host, 2080, 2080).ShouldBeTrue();
        XrayConfigBuilder.Build(Request(definition)).Rejection.ShouldBe(CoreRejection.InvalidEndpoint);
        XrayConfigBuilder.Build(Request(definition with { Port = 3128 })).IsOk.ShouldBeTrue();
        XrayConfigBuilder.Build(Request(definition with { Host = "198.51.100.10" })).IsOk.ShouldBeTrue(
            "совпадение номера порта на устройстве и удаленном сервере само по себе нормально");
    }

    [Fact]
    public void Oneof_cannot_substitute_the_other_forward_protocol()
    {
        var http = Definition(ProxyProtocol.Http, authenticated: true).ToContract("forward-fixture");
        http.Socks5 = http.Http;
        Should.Throw<ArgumentException>(() => http.ToCore());

        var socks = Definition(ProxyProtocol.Socks5, authenticated: true).ToContract("forward-fixture");
        socks.Http = socks.Socks5;
        Should.Throw<ArgumentException>(() => socks.ToCore());
    }

    [Theory]
    [InlineData(TransportSecurity.None)]
    [InlineData(TransportSecurity.Tls)]
    public void Public_udp_dns_is_rejected_for_http_without_fallback_or_rewriting_the_choice(TransportSecurity security)
    {
        var selected = ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "9.9.9.9" };
        var request = Request(Definition(ProxyProtocol.Http, security)) with { Protection = selected };

        XrayConfigBuilder.Build(request).Rejection.ShouldBe(CoreRejection.UnsupportedDns);
        request.Protection.ShouldBe(selected);
        XrayConfigBuilder.Build(request with { Server = Definition(ProxyProtocol.Socks5) }).IsOk.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DnsChoice.Profile, "9.9.9.9")]
    [InlineData(DnsChoice.CloudflareDoh, null)]
    [InlineData(DnsChoice.Quad9Doh, null)]
    [InlineData(DnsChoice.Custom, "https://dns.example.com/dns-query")]
    [InlineData(DnsChoice.Custom, "192.168.0.53")]
    public void Http_preserves_doh_defaults_and_an_explicit_private_dns_exception(DnsChoice choice, string? custom)
    {
        var protection = ProtectionSettings.Default with { Dns = choice, CustomDns = custom };
        var config = XrayConfigBuilder.Build(Request(Definition(ProxyProtocol.Http)) with { Protection = protection }).Config.ShouldNotBeNull();

        config["routing"]!["domainStrategy"]!.GetValue<string>().ShouldBe("IPIfNonMatch", "новый выход не отменяет существующие правила имен и адресов");
        var expected = choice == DnsChoice.Profile ? XrayConfigBuilder.DefaultDns : protection.DnsServers.Single();
        config["dns"]!["servers"]!.AsArray().ShouldContain(server => server!.GetValue<string>() == expected);
    }

    [Fact]
    public void Protocol_labels_encryption_and_service_capability_do_not_claim_more_than_the_selected_transport()
    {
        foreach (var (protocol, security, label, encrypted) in new[]
        {
            (ProxyProtocol.Http, TransportSecurity.None, "HTTP", false),
            (ProxyProtocol.Http, TransportSecurity.Tls, "HTTPS", true),
            (ProxyProtocol.Socks5, TransportSecurity.None, "SOCKS5", false),
        })
        {
            var profile = new ServerProfile("forward-fixture", "Внешний прокси", "", protocol, security, "proxy.example.com", 8443, "tcp", null);
            ServerFacts.Summary(profile).ShouldBe(label);
            ServerFacts.IsEncrypted(profile).ShouldBe(encrypted);
        }

        new ServiceDetails("0.1.0", "1", []).SupportsExternalProxy.ShouldBeFalse();
        new ServiceDetails("0.1.0", "1", [], Capabilities: ["external-proxy-v1"]).SupportsExternalProxy.ShouldBeTrue();
        var credentials = new ServerCredentials(Username: User, Password: Password);
        credentials.ToString().ShouldNotContain(User);
        credentials.ToString().ShouldNotContain(Password);
    }

    private static ServerDefinition Definition(ProxyProtocol protocol, TransportSecurity security = TransportSecurity.None, bool authenticated = false) =>
        new("Внешний прокси", "", protocol, "proxy.example.com", 8443, TransportOptions.Tcp,
            security == TransportSecurity.None ? SecurityOptions.None : new SecurityOptions(security), ProtocolOptions.Empty,
            authenticated ? new ServerCredentials(Username: User, Password: Password) : new ServerCredentials());

    private static CoreRequest Request(ServerDefinition definition) => new(definition, ConnectionMode.Proxy, ProtectionSettings.Default,
        ConnectionSettings.Default with { ProxyPort = 2080, Mux = true }, RoutingSettings.Default);
}
