using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network;

namespace PrivateGateway.Client.Tests.Unit;

public class TrafficEncryptionTests
{
    [Fact]
    public void Vless_without_tls_or_reality_is_not_encrypted()
    {
        TrafficEncryption.IsEncrypted(ProxyProtocol.Vless, TransportSecurity.None).ShouldBeFalse();
    }

    [Theory]
    [InlineData(TransportSecurity.Tls)]
    [InlineData(TransportSecurity.Reality)]
    public void Vless_over_tls_or_reality_is_encrypted(TransportSecurity security)
    {
        TrafficEncryption.IsEncrypted(ProxyProtocol.Vless, security).ShouldBeTrue();
    }

    [Fact]
    public void Vpn_and_forward_protocols_report_their_actual_transport_encryption()
    {
        foreach (var protocol in Enum.GetValues<ProxyProtocol>().Where(p => p != ProxyProtocol.Vless && !ForwardProxyRules.IsForwardProxy(p)))
        {
            foreach (var security in Enum.GetValues<TransportSecurity>())
            {
                TrafficEncryption.IsEncrypted(protocol, security).ShouldBeTrue($"{protocol} + {security}");
            }
        }

        TrafficEncryption.IsEncrypted(ProxyProtocol.Http, TransportSecurity.None).ShouldBeFalse();
        TrafficEncryption.IsEncrypted(ProxyProtocol.Http, TransportSecurity.Tls).ShouldBeTrue();
        TrafficEncryption.IsEncrypted(ProxyProtocol.Socks5, TransportSecurity.None).ShouldBeFalse();
    }
}
