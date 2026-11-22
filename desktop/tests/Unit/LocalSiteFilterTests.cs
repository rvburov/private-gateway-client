using System.Text.Json.Nodes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class LocalSiteFilterTests
{
    [Fact]
    public void Filter_has_no_remote_outbound_and_keeps_exact_blocking_with_private_dispatcher_events()
    {
        var unusableServer = new ServerDefinition("unused", string.Empty, ProxyProtocol.Vless, "remote.example.com", 443,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials());
        var request = new CoreRequest(unusableServer, ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true, Addresses = AddressPreference.Ipv4Only },
            ConnectionSettings.Default,
            RoutingSettings.Default with { Rules = [new DomainRule("blocked.example.com", RuleAction.Block, Exact: true)] },
            LocalSiteFilter: true);
        var config = XrayConfigBuilder.Build(request, new TunLink(10809, "test-only-tun-password", "127.0.0.1")).Config.ShouldNotBeNull();
        var exits = config["outbounds"]!.AsArray();
        exits[0]!["protocol"]!.GetValue<string>().ShouldBe("freedom");
        exits.OfType<JsonObject>().Select(exit => exit["protocol"]!.GetValue<string>()).All(protocol => protocol is "freedom" or "blackhole" or "dns").ShouldBeTrue();
        config.ToJsonString().ShouldNotContain("remote.example.com");
        config["routing"]!["rules"]!.AsArray().Any(rule => rule!["domain"] is JsonArray domains
            && domains.Any(domain => domain?.GetValue<string>() == "full:blocked.example.com")
            && rule["outboundTag"]!.GetValue<string>() == XrayConfigBuilder.BlockTag).ShouldBeTrue();
        config["log"]!["access"]!.GetValue<string>().ShouldBe("none", "access с исходным IP не нужен истории и остается выключен");
        config["log"]!["loglevel"]!.GetValue<string>().ShouldBe("info", "dispatcher сообщает имя после FakeDNS/SNI только в приватный поток службы");
    }

    [Fact]
    public void Local_filter_does_not_claim_hidden_ip_and_round_trips_additive_status()
    {
        var status = ConnectionStatus.Initial(DateTimeOffset.UtcNow).MoveTo(ConnectionState.Connecting, DateTimeOffset.UtcNow)
            .MoveTo(ConnectionState.Connected, DateTimeOffset.UtcNow) with
        {
            LocalSiteFilter = true,
            Exit = new ExitInfo("192.0.2.10", null, null, null, "192.0.2.10"),
            Protection = new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Ok, CheckState.Off, null),
        };
        status.HasExitIpLeak.ShouldBeFalse("реальный IP открыт намеренно, это не обещание VPN");
        status.ToContract().ToCore().LocalSiteFilter.ShouldBeTrue();
        (status with { LocalSiteFilter = false }).HasExitIpLeak.ShouldBeTrue();
    }
}
