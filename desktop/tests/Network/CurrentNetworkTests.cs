using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Сеть машины на главном экране. Служба ее не сообщала, и окно всегда писало «Нет сети», хотя сеть была (у пользователя на
/// Windows 2026-10-30).
/// </summary>
public sealed class CurrentNetworkTests
{
    [Fact]
    public void Adapter_with_a_gateway_names_the_network_and_cable_wins_over_wifi()
    {
        NetworkInspector.Current([(AdapterKind.Wifi, true, true)]).Kind.ShouldBe(NetworkKind.WiFi);
        NetworkInspector.Current([(AdapterKind.Wifi, true, true), (AdapterKind.Ethernet, true, true)]).Kind.ShouldBe(NetworkKind.Ethernet);
        NetworkInspector.Current([(AdapterKind.Cellular, true, true)]).ShouldBe(new NetworkInfo(NetworkKind.Mobile, null, false, true));
        NetworkInspector.Current([(AdapterKind.Other, true, true)]).Kind.ShouldBe(NetworkKind.Ethernet, "виртуальный адаптер со шлюзом — тоже сеть");
    }

    [Fact]
    public void Tunnels_loopback_and_adapters_without_a_gateway_are_not_the_network()
    {
        NetworkInspector.Current([
            (AdapterKind.Tunnel, true, true),
            (AdapterKind.Loopback, true, true),
            (AdapterKind.Ethernet, true, false),
            (AdapterKind.Wifi, false, true),
        ]).ShouldBe(NetworkInfo.Unknown, "свой TUN и чужой VPN — не сеть машины");
    }

    [Fact]
    public async Task Service_reports_the_network_at_start_and_after_it_changes()
    {
        var network = new NetworkInfo(NetworkKind.WiFi, null, false, false);
        var events = new FakeNetwork();
        await using var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"), Network: events,
            CurrentNetwork: () => network, BlockWatch: TimeSpan.FromHours(1)), TimeProvider.System, new ServiceJournal(TimeProvider.System));

        gateway.Status.Network.Kind.ShouldBe(NetworkKind.WiFi, "окно сразу знает сеть, а не пишет «Нет сети»");

        network = new NetworkInfo(NetworkKind.Ethernet, null, false, false);
        events.Raise();

        gateway.Status.Network.Kind.ShouldBe(NetworkKind.Ethernet);
    }
}
