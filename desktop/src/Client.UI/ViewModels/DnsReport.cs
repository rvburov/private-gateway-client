using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// DNS-раздел отчета: выбранные настройки, снимок политики службы и ее диагностический ответ раздельно.
/// Адрес пользовательского DNS намеренно не выводится: путь DoH может содержать токен доступа.
/// Все возвращенные строки дополнительно обезличивает общий построитель отчета.
/// </summary>
public static class DnsReport
{
    public static IEnumerable<string> Lines(AppSettings settings, ConnectionStatus status, ServiceKind service,
        DiagnosticResult? result, CheckRowViewModel? row)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);

        var loc = Loc.Instance;
        var protection = settings.Protection;
        yield return loc["diag.report.dns.title"];
        yield return "  " + loc.Format("diag.report.dns.selected", DnsTexts.Choice(protection.Dns));
        if (protection.Dns == DnsChoice.Custom)
        {
            yield return "  " + loc.Format("diag.report.dns.custom", CustomKind(protection));
        }

        yield return "  " + loc.Format("diag.report.dns.settingsMode", Mode(settings.Mode));
        yield return "  " + loc.Format("diag.report.dns.statusMode", Mode(status.Mode));
        yield return "  " + loc["diag.report.dns.settings"];
        yield return "  " + loc.Format("diag.report.dns.fake", Enabled(protection.FakeDns));
        yield return "  " + loc.Format("diag.report.dns.block", Enabled(protection.BlockOutsideDns));
        yield return "  " + loc.Format("diag.report.dns.lanDns", Enabled(protection.LanDns is not null));
        if (protection.LanDns is not null)
        {
            yield return "  " + loc["diag.report.dns.lanDnsNote"];
        }

        yield return "  " + loc.Format("diag.report.dns.lan", Enabled(protection.AllowLan));
        var split = settings.Routing.Mode == RoutingMode.Split;
        yield return "  " + loc.Format("diag.report.dns.routing", loc[split ? "routing.mode.split" : "routing.mode.all"],
            split ? settings.Routing.Apps.Count(app => app.IsEnabled) : 0,
            split ? settings.Routing.Rules.Count(rule => rule.Action == RuleAction.Direct) : 0);

        var available = service != ServiceKind.None && status.ErrorCode != ErrorCodes.ServiceNotRunning;
        var simulated = service == ServiceKind.Simulated;
        var policy = !available ? loc["diag.report.dns.noService"]
            : simulated ? loc["diag.report.dns.simulated"]
            : status.IsProxy && !status.IsPaused ? loc["diag.report.dns.policy.proxy"]
            : DnsTexts.Policy(status.Protection.Dns);
        yield return "  " + loc.Format("diag.report.dns.policy", policy);

        if (available && !simulated && result?.Check == DiagnosticCheck.Dns && row?.Check == DiagnosticCheck.Dns)
        {
            if (string.IsNullOrWhiteSpace(result.Reason))
            {
                yield return "  " + loc["diag.report.dns.notChecked"];
                yield return "  " + loc["diag.report.dns.legacy"];
            }
            else
            {
                yield return "  " + loc.Format("diag.report.dns.probe", row.ChipText, row.Detail);
            }
        }
        else
        {
            yield return "  " + loc["diag.report.dns.notChecked"];
        }

        yield return "  " + loc["diag.report.dns.scope"];

        string Enabled(bool enabled) => loc[enabled ? "diag.report.dns.on" : "diag.report.dns.off"];
        string Mode(ConnectionMode mode) => loc[mode == ConnectionMode.Vpn ? "settings.mode.vpn" : "settings.mode.proxy"];
    }

    private static string CustomKind(ProtectionSettings protection)
    {
        var kind = string.IsNullOrWhiteSpace(protection.CustomDns) ? "missing"
            : protection.LanDns is not null ? "private"
            : Uri.TryCreate(protection.CustomDns, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? "doh"
            : DnsAddress.Normalize(protection.CustomDns) is not null ? "address"
            : "invalid";
        return Loc.Instance["diag.report.dns.custom." + kind];
    }
}
