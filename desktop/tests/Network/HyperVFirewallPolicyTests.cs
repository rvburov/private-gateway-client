using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

public sealed class HyperVFirewallPolicyTests
{
    [Fact]
    public void Both_enabled_block_directions_are_required_and_order_does_not_matter()
    {
        var rules = Rules();
        HyperVFirewallPolicy.IsClosed(rules).ShouldBeTrue();
        HyperVFirewallPolicy.IsClosed(rules.Reverse().ToArray()).ShouldBeTrue();
        HyperVFirewallPolicy.IsClosed(rules.Select(r => r with { Id = r.Id.ToUpperInvariant() }).ToArray()).ShouldBeTrue();
    }

    [Fact]
    public void Repair_keeps_the_intact_direction_and_does_not_remove_it_for_a_legacy_extra_rule()
    {
        var rules = Rules();
        rules[1] = rules[1] with { Enabled = 2 };

        HyperVFirewallPolicy.CanKeep(HyperVFirewallPolicy.OutboundId, rules).ShouldBeTrue("исходящее направление уже закрыто");
        HyperVFirewallPolicy.CanKeep(HyperVFirewallPolicy.InboundId, rules).ShouldBeFalse("чинится только выключенное входящее направление");

        rules = [.. Rules(), new("legacy", 4, 1, 2)];
        HyperVFirewallPolicy.CanKeep(HyperVFirewallPolicy.OutboundId, rules).ShouldBeTrue();
        HyperVFirewallPolicy.CanKeep(HyperVFirewallPolicy.InboundId, rules).ShouldBeTrue();
        HyperVFirewallPolicy.IsClosed(rules).ShouldBeFalse("лишнее старое правило нужно убрать, сохранив новую пару");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("allow")]
    [InlineData("direction")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("other_id")]
    public void Rule_identifiers_alone_do_not_confirm_a_closed_firewall(string change)
    {
        var rules = Rules();
        rules = change switch
        {
            "disabled" => [rules[0] with { Enabled = 2 }, rules[1]],
            "allow" => [rules[0] with { Action = 2 }, rules[1]],
            "direction" => [rules[0] with { Direction = 1 }, rules[1]],
            "missing" => [rules[0]],
            "duplicate" => [rules[0], rules[0]],
            _ => [rules[0] with { Id = "{11111111-2222-3333-4444-555555555555}" }, rules[1]],
        };

        HyperVFirewallPolicy.IsClosed(rules).ShouldBeFalse("измененные или неполные правила не блокируют оба направления");
    }

    private static HyperVFirewallRule[] Rules() =>
    [
        new("{6a0f5b8e-3c1d-4e7a-9b2f-0d4c8e1a7f53}", 4, 1, 2),
        new("{c4e29d71-58b3-4a06-8f1e-2b7d9c3a6e14}", 4, 1, 1),
    ];
}
