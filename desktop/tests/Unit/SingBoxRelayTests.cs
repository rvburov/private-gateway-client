using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Ретранслятор sing-box для Hysteria2 и TUIC за Xray (решение 452) — конфиг из эталонных ссылок shared/testdata.</summary>
public sealed class SingBoxRelayTests
{
    [Fact]
    public void Each_local_inbound_leads_only_to_its_own_hysteria2_or_tuic_server()
    {
        var config = SingBoxConfigBuilder.BuildRelay(
        [
            new RelayRoute(XrayConfigTests.Link("hysteria2/obfs-insecure"), new RelayLink(31500, "user-0", "pass-0"), "198.51.100.7"),
            new RelayRoute(XrayConfigTests.Link("tuic/bbr"), new RelayLink(31501, "user-1", "pass-1")),
        ]);

        var inbounds = config["inbounds"]!.AsArray();
        inbounds.Select(i => (i!["type"]!.GetValue<string>(), i["tag"]!.GetValue<string>(), i["listen"]!.GetValue<string>(), i["listen_port"]!.GetValue<int>()))
            .ShouldBe([("socks", "relay-in-0", "127.0.0.1", 31500), ("socks", "relay-in-1", "127.0.0.1", 31501)]);
        var user = inbounds[1]!["users"]!.AsArray().ShouldHaveSingleItem()!;
        (user["username"]!.GetValue<string>(), user["password"]!.GetValue<string>()).ShouldBe(("user-1", "pass-1"));
        config["route"]!["rules"]!.AsArray()
            .Select(r => (r!["inbound"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>(), r["outbound"]!.GetValue<string>()))
            .ShouldBe([("relay-in-0", "relay-out-0"), ("relay-in-1", "relay-out-1")]);
        config.ContainsKey("dns").ShouldBeFalse("имена разрешает Xray, а адрес сервера — служба");

        // Hysteria2: адрес, найденный службой, — вместо имени; имя TLS и ALPN — из ссылки, обфускация salamander.
        var hysteria = config["outbounds"]![0]!;
        (hysteria["type"]!.GetValue<string>(), hysteria["tag"]!.GetValue<string>(), hysteria["server"]!.GetValue<string>(), hysteria["server_port"]!.GetValue<int>())
            .ShouldBe(("hysteria2", "relay-out-0", "198.51.100.7", 8443));
        hysteria["password"]!.GetValue<string>().ShouldBe("pw-hy2-1");
        (hysteria["obfs"]!["type"]!.GetValue<string>(), hysteria["obfs"]!["password"]!.GetValue<string>()).ShouldBe(("salamander", "pw-obfs-1"));
        var tls = hysteria["tls"]!;
        (tls["enabled"]!.GetValue<bool>(), tls["server_name"]!.GetValue<string>(), tls["insecure"]!.GetValue<bool>()).ShouldBe((true, "example.com", true));
        tls["alpn"].ShouldBeNull();

        // TUIC: без найденного адреса — имя из ссылки; UUID, пароль, управление перегрузкой и ALPN h3.
        var tuic = config["outbounds"]![1]!;
        (tuic["type"]!.GetValue<string>(), tuic["server"]!.GetValue<string>(), tuic["server_port"]!.GetValue<int>()).ShouldBe(("tuic", "192.0.2.88", 443));
        (tuic["uuid"]!.GetValue<string>(), tuic["password"]!.GetValue<string>(), tuic["congestion_control"]!.GetValue<string>())
            .ShouldBe(("11111111-2222-3333-4444-555555555555", "pw-tuic-1", "bbr"));
        (tuic["tls"]!["server_name"]!.GetValue<string>(), tuic["tls"]!["insecure"]!.GetValue<bool>()).ShouldBe(("example.com", false));
        tuic["tls"]!["alpn"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["h3"]);
    }

    [Fact]
    public void Missing_tls_name_alpn_and_unknown_congestion_control_take_safe_defaults()
    {
        var hysteria = XrayConfigTests.Link("hysteria2/hy2-short-scheme");
        var tuic = XrayConfigTests.Link("tuic/bbr");
        tuic = tuic with
        {
            Security = tuic.Security with { Sni = null, Alpn = null },
            Options = tuic.Options with { CongestionControl = "reno-plus" },
        };

        var config = SingBoxConfigBuilder.BuildRelay([new RelayRoute(hysteria, new RelayLink(31500, "u", "p")), new RelayRoute(tuic, new RelayLink(31501, "u", "p"))]);

        var outbounds = config["outbounds"]!.AsArray();
        outbounds[0]!["tls"]!["server_name"]!.GetValue<string>().ShouldBe("example.com", "без SNI — имя сервера");
        outbounds[0]!["obfs"].ShouldBeNull();
        outbounds[1]!["tls"]!["server_name"]!.GetValue<string>().ShouldBe("192.0.2.88");
        outbounds[1]!["tls"]!["alpn"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["h3"], "у TUIC без ALPN — h3, как у его серверов");
        outbounds[1]!.AsObject().ContainsKey("congestion_control").ShouldBeFalse("незнакомое значение sing-box не запустил бы");
    }

    [Theory]
    [InlineData(CoreLogLevel.Warning, "info")]
    [InlineData(CoreLogLevel.Debug, "debug")]
    public void Log_level_keeps_the_ready_line_and_journal_filters_the_rest(CoreLogLevel level, string expected)
    {
        var config = SingBoxConfigBuilder.BuildRelay([new RelayRoute(XrayConfigTests.Link("tuic/bbr"), new RelayLink(31500, "u", "p"))], level);

        config["log"]!["level"]!.GetValue<string>().ShouldBe(expected, "строка «sing-box started» — уровня info");
        config["route"]!["final"]!.GetValue<string>().ShouldBe("relay-out-0");
    }

    [Fact]
    public void Relay_without_servers_is_a_mistake()
    {
        Should.Throw<ArgumentException>(() => SingBoxConfigBuilder.BuildRelay([]));
    }
}
