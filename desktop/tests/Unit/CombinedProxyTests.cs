using System.Text.Json.Nodes;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class CombinedProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Proxy_input_in_vpn_recovers_fake_dns_even_when_regular_sniffing_is_disabled(bool sniffing)
    {
        var request = new CoreRequest(XrayConfigTests.Link("vless/reality-vision"), ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true },
            ConnectionSettings.Default with { VpnSystemProxy = true, Sniffing = sniffing }, RoutingSettings.Default);
        var config = XrayConfigBuilder.Build(request, new TunLink(30001, "not-a-real-password", "203.0.113.8")).Config.ShouldNotBeNull();
        var local = config["inbounds"]!.AsArray().Single(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.LocalProxyTag)!;
        var options = local["sniffing"]!;
        options["enabled"]!.GetValue<bool>().ShouldBeTrue();
        options["destOverride"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("fakedns");
        options["metadataOnly"]!.GetValue<bool>().ShouldBe(!sniffing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vpn_exposes_a_distinct_loopback_proxy_only_after_explicit_opt_in(bool enabled)
    {
        var connection = ConnectionSettings.Default with { VpnSystemProxy = enabled, ProxyPort = 10809, ProxyAllowLan = true };
        var request = new CoreRequest(XrayConfigTests.Link("vless/reality-vision"), ConnectionMode.Vpn,
            ProtectionSettings.Default with { Addresses = AddressPreference.Ipv4Only }, connection, RoutingSettings.Default);
        var config = XrayConfigBuilder.Build(request, new TunLink(30001, "not-a-real-password", "203.0.113.8")).Config.ShouldNotBeNull();
        var inputs = config["inbounds"]!.AsArray();
        inputs.Select(i => i!["tag"]!.GetValue<string>()).Distinct().Count().ShouldBe(inputs.Count);
        inputs.Single(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.InboundTag)!["protocol"]!.GetValue<string>().ShouldBe("shadowsocks");
        var local = inputs.SingleOrDefault(i => i!["tag"]!.GetValue<string>() == XrayConfigBuilder.LocalProxyTag);
        if (enabled)
        {
            local.ShouldNotBeNull();
            local["listen"]!.GetValue<string>().ShouldBe("127.0.0.1", "режим VPN не открывает новый вход локальной сети");
            local["port"]!.GetValue<int>().ShouldBe(10809);
            local["protocol"]!.GetValue<string>().ShouldBe("socks");
            var ipv6 = config["routing"]!["rules"]!.AsArray().Single(r => r!["ip"]?.ToJsonString() == "[\"::/0\"]")!;
            ipv6["inboundTag"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldContain(XrayConfigBuilder.LocalProxyTag);
            ipv6["outboundTag"]!.GetValue<string>().ShouldBe(XrayConfigBuilder.BlockTag);
        }
        else
        {
            local.ShouldBeNull("старый SystemProxy=true не включает новый вход VPN");
        }
    }

    [Fact]
    public void Proxy_intentions_and_actual_status_round_trip_independently()
    {
        var settings = AppSettings.Default with { Connection = ConnectionSettings.Default with { SystemProxy = false, VpnSystemProxy = true } };
        var restored = settings.ToConnection().ToCore();
        restored.UsesSystemProxy(ConnectionMode.Proxy).ShouldBeFalse();
        restored.UsesSystemProxy(ConnectionMode.Vpn).ShouldBeTrue();
        var status = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
        {
            LocalProxyPort = 10809,
            UserProxyManagedByWindow = true,
            VirtualMachines = new VirtualMachineProtection(CheckState.Warning, "rules_applied"),
        };
        status.ToContract().ToCore().ShouldBe(status);
        new ServiceDetails("0.1.0", "1", []).SupportsVpnProxy.ShouldBeFalse();
        new ServiceDetails("0.1.0", "1", [], Capabilities: ["vpn-local-proxy-v1"]).SupportsVpnProxy.ShouldBeTrue();
    }
}
