using PrivateGateway.Client.Core;
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
    public void Every_protocol_except_plain_vless_is_encrypted_with_any_transport()
    {
        foreach (var protocol in Enum.GetValues<ProxyProtocol>().Where(p => p != ProxyProtocol.Vless))
        {
            foreach (var security in Enum.GetValues<TransportSecurity>())
            {
                TrafficEncryption.IsEncrypted(protocol, security).ShouldBeTrue($"{protocol} + {security}");
            }
        }
    }
}
