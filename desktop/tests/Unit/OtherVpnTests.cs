using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Чужой VPN — туннель другой программы с маршрутом ко всему интернету; свой туннель и сетевые карты не в счет.</summary>
public sealed class OtherVpnTests
{
    [Fact]
    public void Only_tunnels_of_other_programs_count()
    {
        OtherVpn.Find(
        [
            new DefaultRoute("wlan0", IsTunnel: false),
            new DefaultRoute("wg0", IsTunnel: true),
            new DefaultRoute("Private Gateway", IsTunnel: true),
            new DefaultRoute("tun0", IsTunnel: true),
            new DefaultRoute("WG0", IsTunnel: true),
            new DefaultRoute("tun0", IsTunnel: true),
        ], ownAdapter: "private gateway").ShouldBe(["tun0", "wg0"], "свой адаптер — без учета регистра, как имена адаптеров Windows; повторы — один раз");
    }

    [Fact]
    public void Without_tunnels_there_is_no_other_vpn()
    {
        OtherVpn.Find([new DefaultRoute("eth0", IsTunnel: false)], ownAdapter: "pgtun0").ShouldBeEmpty();
        OtherVpn.Find([new DefaultRoute("pgtun0", IsTunnel: true)], ownAdapter: "pgtun0").ShouldBeEmpty();
        OtherVpn.Find([new DefaultRoute("utun3", IsTunnel: true)], ownAdapter: null).ShouldBe(["utun3"]);
    }
}
