namespace PrivateGateway.Client.Platform.Windows;

/// <summary>Снимок нашего правила Hyper-V: одного InstanceID недостаточно, его могли выключить или изменить.</summary>
public sealed record HyperVFirewallRule(string Id, int Action, int Enabled, int Direction);

/// <summary>Проверка обоих направлений блокировки по фактическим свойствам правил, без вызовов Windows.</summary>
public static class HyperVFirewallPolicy
{
    public const int Block = 4;
    public const int Enabled = 1;
    public const string OutboundId = "{6a0f5b8e-3c1d-4e7a-9b2f-0d4c8e1a7f53}";
    public const string InboundId = "{c4e29d71-58b3-4a06-8f1e-2b7d9c3a6e14}";

    public static bool IsClosed(IReadOnlyList<HyperVFirewallRule> rules) => rules.Count == 2
        && rules.Any(r => Matches(r, OutboundId, 2)) && rules.Any(r => Matches(r, InboundId, 1));

    /// <summary>Исправное направление сохраняется при починке другого — без снятия действующего правила.</summary>
    public static bool CanKeep(string id, IReadOnlyList<HyperVFirewallRule> rules)
    {
        var same = rules.Where(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return same.Length == 1 && (Matches(same[0], OutboundId, 2) || Matches(same[0], InboundId, 1));
    }

    private static bool Matches(HyperVFirewallRule rule, string id, int direction) =>
        string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase)
        && rule.Action == Block && rule.Enabled == Enabled && rule.Direction == direction;
}
