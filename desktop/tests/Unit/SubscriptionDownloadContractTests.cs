using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class SubscriptionDownloadContractTests
{
    [Fact]
    public void The_binary_subscription_and_status_survive_the_contract_without_text_recoding()
    {
        var content = new byte[] { 0, 0xff, 0x80, 0x41 };
        var result = new SubscriptionDownload(content, "Заголовок").ToContract().ToCore();
        result.Content.ShouldBe(content);
        result.Title.ShouldBe("Заголовок");
        result.Failure.ShouldBeNull();

        var failure = SubscriptionDownload.Failed(DownloadFailure.Status, 403).ToContract().ToCore();
        failure.Failure.ShouldBe(DownloadFailure.Status);
        failure.StatusCode.ShouldBe(403);
        failure.Content.ShouldBeNull();
        new V1.SubscriptionDownloadReply { Failure = "future-unknown-reason" }.ToCore().Failure.ShouldBe(DownloadFailure.Network);

        foreach (var reason in Enum.GetValues<DownloadFailure>())
        {
            SubscriptionDownload.Failed(reason).ToContract().ToCore().Failure.ShouldBe(reason);
        }
    }

    [Theory]
    [InlineData(ProxyProtocol.WireGuard, DnsChoice.Custom, "192.168.1.1", true)]
    [InlineData(ProxyProtocol.WireGuard, DnsChoice.Custom, "fd00::53", true)]
    [InlineData(ProxyProtocol.WireGuard, DnsChoice.CloudflareDoh, null, false)]
    [InlineData(ProxyProtocol.WireGuard, DnsChoice.Custom, "https://1.1.1.1/dns-query", false)]
    [InlineData(ProxyProtocol.WireGuard, DnsChoice.Custom, "9.9.9.9", false)]
    [InlineData(ProxyProtocol.Vless, DnsChoice.Custom, "192.168.1.1", false)]
    public void Wireguard_with_an_applied_lan_dns_is_refused_before_sending_a_subscription(ProxyProtocol protocol, DnsChoice dns, string? address, bool refused)
    {
        var protection = ProtectionSettings.Default with { Dns = dns, CustomDns = address };
        var result = SubscriptionDownload.RefuseUnsafeDns(protocol, XrayConfigBuilder.DnsMayBypassServer(protection));
        if (refused)
        {
            result.ShouldNotBeNull().Failure.ShouldBe(DownloadFailure.UnsafeDns);
            result.Content.ShouldBeNull();
        }
        else
        {
            result.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("https://192.168.1.53/dns-query", true, true)]
    [InlineData("https://192.168.1.53/dns-query", false, false)]
    [InlineData("https://127.0.0.1/dns-query", true, true)]
    [InlineData("https://100.64.0.53/dns-query", true, true)]
    [InlineData("https://[fd00::53]/dns-query", true, true)]
    [InlineData("https://[::ffff:192.168.1.53]/dns-query", true, true)]
    [InlineData("127.0.0.53", true, true)]
    [InlineData("100.64.0.53", true, true)]
    [InlineData("100.64.0.53", false, false)]
    [InlineData("https://1.1.1.1/dns-query", true, false)]
    [InlineData("https://9.9.9.9/dns-query", false, false)]
    [InlineData("https://dns.example.com/dns-query", true, true)]
    [InlineData("https://dns.example.com/dns-query", false, false)]
    public void Wireguard_uses_the_same_direct_dns_boundaries_as_the_generated_config(string dns, bool allowLan, bool refused)
    {
        var protection = ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = dns, AllowLan = allowLan };
        var result = SubscriptionDownload.RefuseUnsafeDns(ProxyProtocol.WireGuard, XrayConfigBuilder.DnsMayBypassServer(protection));
        (result?.Failure == DownloadFailure.UnsafeDns).ShouldBe(refused);
    }
}
