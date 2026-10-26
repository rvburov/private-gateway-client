using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

namespace PrivateGateway.Client.Tests.Unit;

public class ServerListTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 15, 42, 0, TimeSpan.Zero);

    private static readonly ServerSubscription Main = new("main", "Основная подписка", Now);
    private static readonly ServerSubscription Spare = new("spare", "Резервная", null);

    private static readonly ServerProfile Nl = new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless,
        TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42, "Амстердам", "main", IsFavorite: true, LastUsedAt: Now);

    private static readonly ServerProfile Fi = new("fi", "Финляндия 01", "FI", ProxyProtocol.Hysteria2,
        TransportSecurity.Tls, "203.0.113.57", 8443, "quic", 38, "Хельсинки", "main", IsFavorite: true);

    private static readonly ServerProfile De = new("de", "Германия 02", "DE", ProxyProtocol.Vless,
        TransportSecurity.Tls, "198.51.100.140", 443, "xhttp", 51, "Франкфурт", "spare", LastUsedAt: Now.AddDays(-1));

    private static readonly ServerProfile Us = new("us", "США 01", "US", ProxyProtocol.Trojan,
        TransportSecurity.Tls, "198.51.100.201", 443, "ws", 148, "Нью-Йорк");

    private static readonly ServerProfile Tr = new("tr", "Турция 01", "TR", ProxyProtocol.Vless,
        TransportSecurity.Tls, "192.0.2.150", 443, "grpc", null, "Стамбул", "main");

    private static readonly ServerProfile[] All = [Nl, Fi, De, Us, Tr];

    [Fact]
    public void All_tab_groups_by_subscription_in_their_order_and_manual_servers_last()
    {
        var groups = Build(ServerListQuery.Default);

        groups.Select(g => g.Subscription?.Id).ShouldBe(["main", "spare", null]);
        groups[0].Servers.Select(s => s.Id).ShouldBe(["fi", "nl", "tr"], "по пингу, недоступный — в конце");
        groups[1].Servers.Select(s => s.Id).ShouldBe(["de"]);
        groups[2].Servers.Select(s => s.Id).ShouldBe(["us"]);
    }

    [Fact]
    public void Server_of_a_removed_subscription_is_shown_with_manual_ones()
    {
        var orphan = Fi with { Id = "orphan", SubscriptionId = "gone" };

        var groups = ServerList.Build([orphan, Us], [Main], ServerListQuery.Default, Ru);

        groups.ShouldHaveSingleItem().Subscription.ShouldBeNull();
        groups[0].Servers.Select(s => s.Id).ShouldBe(["orphan", "us"]);
    }

    [Theory]
    [InlineData("германия", "de")]
    [InlineData("ХЕЛЬСИНКИ", "fi")]
    [InlineData("us", "us")]
    [InlineData("198.51.100.2", "us")]
    [InlineData("hysteria", "fi")]
    [InlineData("  нью  ", "us")]
    public void Search_finds_by_name_city_country_address_and_protocol(string search, string expected)
    {
        var groups = Build(ServerListQuery.Default with { Search = search });

        groups.SelectMany(g => g.Servers).Select(s => s.Id).ShouldBe([expected]);
    }

    [Fact]
    public void Empty_search_matches_everything()
    {
        ServerList.Matches(Nl, "   ").ShouldBeTrue();
        ServerList.Matches(Nl, "Польша").ShouldBeFalse();
    }

    [Fact]
    public void Hidden_protocols_are_filtered_out()
    {
        var query = ServerListQuery.Default with { HiddenProtocols = new HashSet<ProxyProtocol> { ProxyProtocol.Vless } };

        Build(query).SelectMany(g => g.Servers).Select(s => s.Id).ShouldBe(["fi", "us"]);
    }

    [Fact]
    public void Sort_by_name_follows_the_language_alphabet()
    {
        var query = ServerListQuery.Default with { Tab = ServerListTab.Favorites, Sort = ServerSort.Name };

        Build(query).ShouldHaveSingleItem().Servers.Select(s => s.Name).ShouldBe(["Нидерланды 01", "Финляндия 01"]);
    }

    [Fact]
    public void Favorites_tab_is_one_group_of_starred_servers()
    {
        var groups = Build(ServerListQuery.Default with { Tab = ServerListTab.Favorites });

        groups.ShouldHaveSingleItem().Subscription.ShouldBeNull();
        groups[0].Servers.Select(s => s.Id).ShouldBe(["fi", "nl"]);
    }

    [Fact]
    public void Recent_tab_keeps_only_the_last_chosen_servers()
    {
        var many = Enumerable.Range(0, ServerList.RecentLimit + 5)
            .Select(i => Us with { Id = "s" + i, LatencyMs = 100 - i, LastUsedAt = Now.AddMinutes(-i) })
            .Append(Tr)
            .ToList();

        var rows = ServerList.Build(many, [], ServerListQuery.Default with { Tab = ServerListTab.Recent }, Ru)
            .ShouldHaveSingleItem().Servers;

        rows.Count.ShouldBe(ServerList.RecentLimit);
        rows.ShouldNotContain(s => s.Id == "tr", "его ни разу не выбирали");
        rows.Select(s => s.Id).ShouldBe(Enumerable.Range(0, ServerList.RecentLimit).Select(i => "s" + i),
            "свежие сверху; задержка порядок не меняет");
    }

    [Fact]
    public void Empty_tab_has_no_groups()
    {
        ServerList.Build([Us], [], ServerListQuery.Default with { Tab = ServerListTab.Favorites }, Ru).ShouldBeEmpty();
    }

    [Fact]
    public void Filter_lists_only_present_protocols_in_mockup_order()
    {
        ServerList.ProtocolsIn(All).ShouldBe([ProxyProtocol.Vless, ProxyProtocol.Hysteria2, ProxyProtocol.Trojan]);
        ServerList.ProtocolOrder.Count.ShouldBe(Enum.GetValues<ProxyProtocol>().Length, "в порядке нет ни одного протокола");
    }

    [Theory]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.Reality, "tcp", "VLESS · REALITY")]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.Reality, "grpc", "VLESS · REALITY")]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.Tls, "xhttp", "VLESS · XHTTP")]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.Tls, "grpc", "VLESS · gRPC")]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.Tls, "tcp", "VLESS · TLS")]
    [InlineData(ProxyProtocol.Vless, TransportSecurity.None, "tcp", "VLESS")]
    [InlineData(ProxyProtocol.Trojan, TransportSecurity.Tls, "ws", "Trojan · WS")]
    [InlineData(ProxyProtocol.Trojan, TransportSecurity.Tls, "tcp", "Trojan")]
    [InlineData(ProxyProtocol.Hysteria2, TransportSecurity.Tls, "quic", "Hysteria2")]
    [InlineData(ProxyProtocol.Tuic, TransportSecurity.Tls, "quic", "TUIC v5")]
    [InlineData(ProxyProtocol.WireGuard, TransportSecurity.None, "udp", "WireGuard")]
    [InlineData(ProxyProtocol.Vmess, TransportSecurity.None, "httpupgrade", "VMess · HTTPUpgrade")]
    public void Summary_names_what_distinguishes_the_server(
        ProxyProtocol protocol, TransportSecurity security, string transport, string expected)
    {
        var server = Us with { Protocol = protocol, Security = security, Transport = transport };

        ServerFacts.Summary(server).ShouldBe(expected);
    }

    [Fact]
    public void Server_in_use_comes_from_the_service_while_protection_is_on()
    {
        var catalog = new ConnectionControllerTests.FakeCatalog(Nl, Fi) { SelectedId = "nl" };
        var working = ConnectionStatus.Initial(Now) with { State = ConnectionState.Reconnecting, ServerId = "fi" };

        ServerFacts.InUse(catalog, working).ShouldBe(Fi, "выбранный сменили, а защита идет через прежний — показывать его");
        ServerFacts.InUse(catalog, working with { State = ConnectionState.Off }).ShouldBe(Nl, "защита выключена — выбранный");
        ServerFacts.InUse(catalog, working with { ServerId = "gone" }).ShouldBe(Nl, "сервера нет в списке — выбранный");
    }

    [Fact]
    public void Demo_catalog_matches_the_mockup_list()
    {
        var catalog = new DemoServerCatalog(new FakeTimeProvider(Now));

        catalog.Servers.Select(s => s.Host + " · " + ServerFacts.Summary(s)).ShouldBe(
        [
            "203.0.113.24 · VLESS · REALITY",
            "203.0.113.57 · Hysteria2",
            "198.51.100.140 · VLESS · XHTTP",
            "192.0.2.33 · WireGuard",
            "192.0.2.88 · TUIC v5",
            "198.51.100.201 · Trojan · WS",
            "192.0.2.150 · VLESS · gRPC",
        ]);
        catalog.Selected!.Name.ShouldBe("Нидерланды 01");
    }

    [Fact]
    public void Choosing_a_server_puts_it_into_recent()
    {
        var time = new FakeTimeProvider(Now);
        var catalog = new DemoServerCatalog(time);
        var changes = 0;
        catalog.Changed += (_, _) => changes++;
        time.Advance(TimeSpan.FromMinutes(5));

        catalog.Choose("demo-us-01");
        catalog.Choose("demo-us-01");

        changes.ShouldBe(1, "повторный выбор того же сервера ничего не меняет");
        catalog.Selected!.LastUsedAt.ShouldBe(Now.AddMinutes(5));
        ServerList.Build(catalog.Servers, catalog.Subscriptions, ServerListQuery.Default with
        {
            Tab = ServerListTab.Recent,
            Sort = ServerSort.Name,
        }, Ru).ShouldHaveSingleItem().Servers.Select(s => s.Id).ShouldContain("demo-us-01");
    }

    [Fact]
    public void Star_is_saved_and_raises_change_once()
    {
        var catalog = new DemoServerCatalog(new FakeTimeProvider(Now));
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        catalog.SetFavorite("demo-tr-01", true);
        catalog.SetFavorite("demo-tr-01", true);
        catalog.SetFavorite("nobody", true);

        changes.ShouldBe(1);
        catalog.Servers.Single(s => s.Id == "demo-tr-01").IsFavorite.ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_updates_subscription_time_after_the_delay()
    {
        var time = new FakeTimeProvider(Now);
        var catalog = new DemoServerCatalog(time);

        var refresh = catalog.RefreshAsync(TestContext.Current.CancellationToken);
        refresh.IsCompleted.ShouldBeFalse();
        time.Advance(DemoServerCatalog.RefreshDelay);
        await refresh;

        catalog.Subscriptions.ShouldHaveSingleItem().UpdatedAt.ShouldBe(Now + DemoServerCatalog.RefreshDelay);
    }

    private static IReadOnlyList<ServerGroup> Build(ServerListQuery query) =>
        ServerList.Build(All, [Main, Spare], query, Ru);
}
