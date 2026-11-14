using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Отказ чтения правил не подтверждает снятие блока; все вызовы ОС здесь заменены ответами стенда.</summary>
public sealed class FirewallReadbackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-firewall-readback-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Permission denied")]
    [InlineData("Device not configured")]
    public void Unreadable_pf_rules_do_not_confirm_that_the_block_was_removed(string error)
    {
        var pf = new PfKillSwitch((arguments, _) => arguments.Contains("-s")
            ? (1, string.Empty, error) : (0, string.Empty, string.Empty), Path.Combine(_root, "pf.token"));

        pf.Disable();
        Should.Throw<IOException>(() => pf.IsEnabled).Message.ShouldContain(error);
        Should.Throw<IOException>(() => pf.IsIntact).Message.ShouldContain(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public void Only_successfully_read_empty_pf_rules_confirm_an_absent_block(string rules)
    {
        var pf = new PfKillSwitch((_, _) => (0, rules, string.Empty), Path.Combine(_root, "pf.token"));
        pf.IsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, "  com.apple\n  com.apple/250.Other\n", false)]
    [InlineData(0, "  com.apple\n  com.apple/250.PrivateGateway2\n", false)]
    [InlineData(0, "  com.apple\n  com.apple/250.PrivateGateway\n", true)]
    [InlineData(1, "", true)]
    public void Missing_pf_anchor_requires_a_successful_recursive_inventory(int listCode, string anchors, bool readFailed)
    {
        var requests = new List<string>();
        var pf = new PfKillSwitch((arguments, _) =>
        {
            var command = string.Join(' ', arguments);
            requests.Add(command);
            return command == "-v -s Anchors"
                ? (listCode, anchors, "anchors unavailable")
                : (1, string.Empty, "pfctl: DIOCGETRULES: Invalid argument");
        }, Path.Combine(_root, "pf.token"));

        if (readFailed)
        {
            Should.Throw<IOException>(() => pf.IsEnabled).Message.ShouldContain("DIOCGETRULES");
        }
        else
        {
            pf.IsEnabled.ShouldBeFalse();
        }
        requests.ShouldBe(["-a com.apple/250.PrivateGateway -s rules", "-v -s Anchors"]);
    }

    [Fact]
    public void Filter_lookup_distinguishes_a_missing_filter_from_a_failed_query()
    {
        WfpFilterQuery.Found(0).ShouldBeTrue();
        WfpFilterQuery.Found(0x80320003).ShouldBeFalse("FWP_E_FILTER_NOT_FOUND подтверждает отсутствие");
        Should.Throw<IOException>(() => WfpFilterQuery.Found(5)).Message.ShouldContain("0x00000005");
        Should.Throw<IOException>(() => WfpFilterQuery.Found(1722)).Message.ShouldContain("0x000006BA");
    }

    [Fact]
    public void Failed_table_query_does_not_report_that_the_linux_block_is_absent()
    {
        var killSwitch = new NftablesKillSwitch((arguments, _) => arguments[0] == "list"
            ? throw new IOException("nft: Operation not permitted") : string.Empty);

        killSwitch.Disable();
        Should.Throw<IOException>(() => killSwitch.IsEnabled).Message.ShouldContain("Operation not permitted");
    }

    [Fact]
    public void Enumeration_keeps_our_scattered_filters_on_later_pages_and_preserves_foreign_owners()
    {
        var provider = new Guid("11111111-2222-3333-4444-555555555555");
        var sublayer = new Guid("66666666-7777-8888-9999-AAAAAAAAAAAA");
        var other = new Guid("BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF");
        var ownFirst = new WfpFilterEntry(WfpRules.FilterKey(64), provider, sublayer, 0);
        var ownLast = new WfpFilterEntry(WfpRules.FilterKey(500), provider, sublayer, 1);
        IReadOnlyList<WfpFilterEntry>[] pages =
        [
            [.. Enumerable.Range(0, 128).Select(i => new WfpFilterEntry(WfpRules.FilterKey(i), other, sublayer, 0))],
            [ownFirst, new(WfpRules.FilterKey(0), provider, other, 1)],
            [new(WfpRules.FilterKey(1), null, sublayer, 1), ownLast],
        ];

        WfpFilterQuery.Owned(pages, provider, sublayer).ShouldBe([ownFirst, ownLast],
            "свой провайдер И подслой, все страницы, без предположения о непрерывных номерах ключей");
    }

    [Fact]
    public void Failed_later_page_cannot_be_used_as_a_complete_filter_inventory()
    {
        var provider = Guid.NewGuid();
        var sublayer = Guid.NewGuid();
        IEnumerable<IReadOnlyList<WfpFilterEntry>> Pages()
        {
            yield return [new(WfpRules.FilterKey(80), provider, sublayer, 0)];
            throw new IOException("WFP: later page unavailable");
        }

        Should.Throw<IOException>(() => WfpFilterQuery.Owned(Pages(), provider, sublayer)).Message.ShouldContain("later page");
    }

    [Theory]
    [InlineData(WfpFilterQuery.NotFound)]
    [InlineData(WfpFilterQuery.ProviderNotFound)]
    [InlineData(WfpFilterQuery.SublayerNotFound)]
    public void A_rule_or_owner_still_in_use_is_a_failed_removal(uint absent)
    {
        WfpFilterQuery.RequireRemoved(0, absent, "test");
        WfpFilterQuery.RequireRemoved(absent, absent, "test");
        Should.Throw<IOException>(() => WfpFilterQuery.RequireRemoved(0x8032000A, absent, "test"))
            .Message.ShouldContain("0x8032000A", customMessage: "FWP_E_IN_USE не подтверждает снятие оставшихся правил");
        Should.Throw<IOException>(() => WfpFilterQuery.RequireRemoved(5, absent, "test"));
    }

}
