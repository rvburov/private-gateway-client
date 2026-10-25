using PrivateGateway.Client.Core.Logging;

namespace PrivateGateway.Client.Tests.Unit;

// Строки — как в выводе Xray 25.8 и sing-box 1.14 на уровнях «Информация» и «Отладка»; адреса ненастоящие.
public class AddressMaskerTests
{
    private const string Hidden = AddressMasker.Hidden;

    [Theory]
    [InlineData("2026/10/25 14:40:32.278469 [Info] [1234] proxy/vless/outbound: tunneling request to tcp:example.com:443 via 203.0.113.1:443",
        "2026/10/25 14:40:32.278469 [Info] [1234] proxy/vless/outbound: tunneling request to tcp:" + Hidden + ":443 via " + Hidden + ":443")]
    [InlineData("[Info] [77] app/dispatcher: sniffed domain: www.example.org", "[Info] [77] app/dispatcher: sniffed domain: " + Hidden)]
    [InlineData("[Info] [77] app/dispatcher: taking detour [proxy] for [tcp:198.51.100.7:80]", "[Info] [77] app/dispatcher: taking detour [proxy] for [tcp:" + Hidden + ":80]")]
    [InlineData("INFO [3870541312 0ms] inbound/tun[tun-in]: inbound connection to [2001:db8::1]:443", "INFO [3870541312 0ms] inbound/tun[tun-in]: inbound connection to [" + Hidden + "]:443")]
    [InlineData("INFO [1 0ms] inbound/tun[tun-in]: inbound connection from 172.19.0.1:40000", "INFO [1 0ms] inbound/tun[tun-in]: inbound connection from " + Hidden + ":40000")]
    [InlineData("ERROR dial tcp [fe80::1%eth0]:53: connect: refused", "ERROR dial tcp [" + Hidden + "]:53: connect: refused")]
    [InlineData("DEBUG router: match ip_cidr 2001:db8:0:1::/64", "DEBUG router: match ip_cidr " + Hidden + "/64")]
    public void Addresses_of_connections_are_hidden(string line, string masked)
    {
        AddressMasker.Mask(line).ShouldBe(masked);
    }

    [Theory]
    [InlineData("Xray 25.8.3 (Xray, Penetrates Everything.) Custom (go1.24.7 linux/amd64)")]
    [InlineData("2026/10/25 14:40:32.278469 [Info] infra/conf/serial: Reading config: &{Name:stdin: Format:json}")]
    [InlineData("[Warning] core: Xray 25.8.3 started")]
    [InlineData("INFO sing-box started (0.31s)")]
    [InlineData("failed to load geoip.dat: app/router/condition.go:52")]
    [InlineData("WARN inbound/tun[tun-in]: open interface take too much time to finish!")]
    public void Versions_times_and_files_stay(string line)
    {
        AddressMasker.Mask(line).ShouldBe(line);
    }
}
