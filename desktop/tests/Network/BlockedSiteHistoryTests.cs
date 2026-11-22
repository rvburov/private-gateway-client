using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

public sealed class BlockedSiteHistoryTests
{
    [Theory]
    [InlineData("[Warning] quoted [Info] [1234] app/dispatcher: taking detour [block] for [tcp:quoted.example.com:443]")]
    [InlineData("2026/11/23 10:00:00 [Warning] [1234] app/dispatcher: quoted [Info] taking detour [block] for [tcp:quoted.example.com:443]")]
    [InlineData("prefix [Info] app/dispatcher: taking detour [block] for [tcp:quoted.example.com:443]")]
    [InlineData("[Info] app/dispatcher: quoted taking detour [block] for [tcp:quoted.example.com:443]")]
    [InlineData("[Info] app/dispatcher: taking detour [block] for [tcp:quoted.example.com:443] quoted suffix")]
    public void Embedded_info_and_quoted_routes_are_not_actual_dispatcher_events(string line)
    {
        var history = new BlockedSiteHistory(TimeProvider.System);
        history.Consume(line).ShouldBeFalse();
        history.Snapshot().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("[Info] app/dispatcher: ")]
    [InlineData("[Info] [1234] app/dispatcher: ")]
    [InlineData("2026/11/23 10:00:00.123456 [Info] [1234] app/dispatcher: ")]
    [InlineData("2026/11/23 10:00:00 [Info] [1234] app/dispatcher: Hit route rule: [site-rule] so ")]
    public void Actual_optional_timestamp_session_and_named_rule_prefixes_are_supported(string prefix)
    {
        var history = new BlockedSiteHistory(TimeProvider.System);
        history.Consume(prefix + "taking detour [block] for [tcp:actual.example.com:443]").ShouldBeTrue();
        history.Snapshot().ShouldHaveSingleItem().Host.ShouldBe("actual.example.com");
    }

    [Fact]
    public void An_oversized_route_is_rejected_before_regex_matching()
    {
        var history = new BlockedSiteHistory(TimeProvider.System);
        history.Consume("[Info] app/dispatcher: taking detour [block] for [tcp:" + new string('a', 4096) + ".example.com:443]").ShouldBeFalse();
        history.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Only_actual_blocked_destinations_are_kept_and_all_access_records_are_consumed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        var history = new BlockedSiteHistory(time);
        history.Consume("2026/11/23 10:00:00 from tcp:127.0.0.1:4321 accepted tcp:example.com:443 [socks-in -> proxy]").ShouldBeTrue();
        history.Consume("from tcp:127.0.0.1:4321 rejected tcp:secret.example:443 bad request").ShouldBeTrue();
        history.Consume("from tcp:127.0.0.1:4321 accepted tcp:probe.example:443 [probe-in -> block]").ShouldBeTrue();
        history.Consume(Routed("tcp:allowed.example.com:443", "proxy")).ShouldBeTrue();
        history.Consume(Routed("tcp:example.com:443")).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(3));
        history.Consume(Routed("udp:example.com:443")).ShouldBeTrue();
        history.Consume("Xray started").ShouldBeFalse();
        var item = history.Snapshot().ShouldHaveSingleItem();
        (item.Host, item.Count, item.LastSeen).ShouldBe(("example.com", 2, time.GetUtcNow()));
    }

    [Theory]
    [InlineData("tcp:example.com/private:443")]
    [InlineData("tcp:user@example.com:443")]
    [InlineData("tcp:198.18.1.2:443")]
    [InlineData("tcp:*.example.com:443")]
    [InlineData("tcp:geosite:youtube:443")]
    [InlineData("tcp:example.com:0")]
    [InlineData("tcp:example.com:65536")]
    public void Malformed_destinations_and_unresolved_fake_addresses_cannot_be_allowed(string destination)
    {
        var history = new BlockedSiteHistory(TimeProvider.System);
        history.Consume(Routed(destination)).ShouldBeTrue();
        history.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Capacity_evicts_oldest_day_change_and_explicit_clear_remove_private_history()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        var history = new BlockedSiteHistory(time);
        for (var i = 0; i <= BlockedSiteHistory.Limit; i++)
        {
            history.Consume(Routed($"tcp:site{i}.example.com:443"));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        var sites = history.Snapshot();
        sites.Count.ShouldBe(BlockedSiteHistory.Limit);
        sites.ShouldNotContain(site => site.Host == "site0.example.com");
        sites[0].Host.ShouldBe($"site{BlockedSiteHistory.Limit}.example.com");
        time.Advance(TimeSpan.FromDays(1));
        history.Snapshot().ShouldBeEmpty();
        history.Consume(Routed("tcp:example.com:443"));
        history.Clear();
        history.Snapshot().ShouldBeEmpty();
    }

    private static string Routed(string destination, string outbound = "block") =>
        $"2026/11/23 10:00:00 [Info] [1234] app/dispatcher: taking detour [{outbound}] for [{destination}]";

    [Fact]
    public void Old_session_cannot_repopulate_history_after_sign_out_clear()
    {
        var history = new BlockedSiteHistory(TimeProvider.System);
        history.Clear();
        history.Consume(Routed("tcp:old.example.com:443"), () => false).ShouldBeTrue();
        history.Snapshot().ShouldBeEmpty();
        history.Consume(Routed("tcp:new.example.com:443"), () => true).ShouldBeTrue();
        history.Snapshot().ShouldHaveSingleItem().Host.ShouldBe("new.example.com");
    }
}
