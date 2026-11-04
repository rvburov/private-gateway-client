using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>Имена DNS-настроек и состояния политики: без пользовательских адресов и обещания результата сетевой пробы.</summary>
public static class DnsTexts
{
    public static string Choice(DnsChoice choice) => Loc.Instance[choice switch
    {
        DnsChoice.CloudflareDoh => "prot.dns.cloudflare",
        DnsChoice.Quad9Doh => "prot.dns.quad9",
        DnsChoice.Custom => "prot.dns.custom",
        _ => "prot.dns.default",
    }];

    public static string Policy(CheckState state) => Loc.Instance[state switch
    {
        CheckState.Ok => "diag.report.dns.policy.ok",
        CheckState.Waiting => "diag.report.dns.policy.waiting",
        CheckState.Stopped => "diag.report.dns.policy.stopped",
        CheckState.Off => "diag.report.dns.policy.off",
        _ => "diag.report.dns.policy.warning",
    }];
}
