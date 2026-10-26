using System.Net;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ProtectionSettingsTests
{
    [Theory]
    [InlineData("10.0.0.53", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.1.53", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fd00::53", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.53", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("9.9.9.9", false)]
    [InlineData("2001:db8::53", false)]
    public void Private_networks_are_the_ones_the_local_network_setting_opens(string address, bool lan) =>
        PrivateNetwork.Contains(IPAddress.Parse(address)).ShouldBe(lan);

    [Fact]
    public void Own_dns_in_a_private_network_is_only_a_custom_ip_address()
    {
        (ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "192.168.1.53" }).LanDns.ShouldBe(IPAddress.Parse("192.168.1.53"));
        (ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "9.9.9.9" }).LanDns.ShouldBeNull("адрес в интернете — через сервер");
        (ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "https://192.168.1.53/dns-query" }).LanDns.ShouldBeNull("DoH — по имени и порту 443");
        (ProtectionSettings.Default with { Dns = DnsChoice.CloudflareDoh, CustomDns = "192.168.1.53" }).LanDns.ShouldBeNull("свой адрес не выбран");
    }
}
