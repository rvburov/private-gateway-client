using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>DNS-отчет различает желаемые настройки, снимок политики и ограниченную пробу, не раскрывая адреса.</summary>
public sealed class DnsReportTests : WindowHarness
{
    [AvaloniaFact]
    public void Selected_settings_and_applied_policy_do_not_replace_an_inconclusive_probe()
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            Protection = ProtectionSettings.Default with { FakeDns = true, BlockOutsideDns = false, AllowLan = true },
            Routing = new RoutingSettings(RoutingMode.Split,
                [new BypassApp("/private/browser", "private app"), new BypassApp("/private/off", "disabled", false)],
                [new DomainRule("private.example.com", RuleAction.Direct), new DomainRule("blocked.example.com", RuleAction.Block)]),
        };
        var status = Connected(ConnectionMode.Vpn, CheckState.Ok);
        var result = new DiagnosticResult(DiagnosticCheck.Dns, CheckState.Warning, "dns_no_answer");
        var row = new CheckRowViewModel(DiagnosticCheck.Dns, "DNS", "Тестовый ответ пробы не получен", "Нет ответа", CheckState.Warning);

        var report = string.Join('\n', DnsReport.Lines(settings, status, ServiceKind.Installed, result, row));

        report.ShouldContain("Выбрано в настройках: По умолчанию: Cloudflare");
        report.ShouldContain("Режим в настройках: Только прокси");
        report.ShouldContain("Режим в снимке службы: VPN · вся система");
        report.ShouldContain("FakeDNS в настройках: включено");
        report.ShouldContain("Запрет DNS вне туннеля в настройках: выключено");
        report.ShouldContain("DNS-защита по снимку службы: действует по данным службы");
        report.ShouldContain("Ответ проверки службы: [Нет ответа] Тестовый ответ пробы не получен");
        report.ShouldContain("IPv4 UDP/53");
        report.ShouldContain("DoH/DoT");
        report.ShouldContain("включенных прямых исключений приложений: 1; прямых правил сайтов: 1");
        report.ShouldNotContain("/private/browser");
        report.ShouldNotContain("private.example.com");
        report.ShouldNotContain("утечек нет");
    }

    [AvaloniaTheory]
    [InlineData(ServiceKind.None, null, "неизвестна: служба не отвечает")]
    [InlineData(ServiceKind.Installed, ErrorCodes.ServiceNotRunning, "неизвестна: служба не отвечает")]
    [InlineData(ServiceKind.Simulated, null, "имитация; фактическая политика ОС не проверена")]
    public void Missing_or_simulated_service_does_not_reuse_a_successful_policy_or_probe(ServiceKind service, string? error, string expected)
    {
        var status = Connected(ConnectionMode.Vpn, CheckState.Ok) with { ErrorCode = error };
        var result = new DiagnosticResult(DiagnosticCheck.Dns, CheckState.Warning, "dns_open");
        var row = new CheckRowViewModel(DiagnosticCheck.Dns, "DNS", "старый ответ не должен остаться", "старый ответ", CheckState.Warning);

        var report = string.Join('\n', DnsReport.Lines(AppSettings.Default, status, service, result, row));

        report.ShouldContain(expected);
        report.ShouldContain("Нет актуального результата сетевой пробы");
        report.ShouldNotContain("действует по данным службы");
        report.ShouldNotContain("старый ответ");
    }

    [AvaloniaFact]
    public void No_result_and_legacy_policy_without_reason_do_not_become_a_completed_probe()
    {
        var status = Connected(ConnectionMode.Vpn, CheckState.Ok);
        var row = new CheckRowViewModel(DiagnosticCheck.Dns, "DNS", "безусловное обещание", "OK", CheckState.Ok);

        var missing = string.Join('\n', DnsReport.Lines(AppSettings.Default, status, ServiceKind.Installed, null, row));
        var legacy = string.Join('\n', DnsReport.Lines(AppSettings.Default, status, ServiceKind.Installed,
            new DiagnosticResult(DiagnosticCheck.Dns, CheckState.Ok), row));

        foreach (var report in new[] { missing, legacy })
        {
            report.ShouldContain("действует по данным службы");
            report.ShouldContain("Нет актуального результата сетевой пробы");
            report.ShouldNotContain("безусловное обещание");
            report.ShouldNotContain("[OK]");
        }
        legacy.ShouldContain("Служба не передала уточнение сетевой пробы");
    }

    [AvaloniaFact]
    public void Proxy_policy_and_private_dns_exception_are_explicit_without_listing_endpoint_or_inactive_rules()
    {
        var settings = AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            Protection = ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "192.168.50.53" },
            Routing = new RoutingSettings(RoutingMode.AllTraffic,
                [new BypassApp("/private/browser", "private app")], [new DomainRule("private.example.com", RuleAction.Direct)]),
        };

        var report = string.Join('\n', DnsReport.Lines(settings, Connected(ConnectionMode.Proxy, CheckState.Warning),
            ServiceKind.User, null, null));

        report.ShouldContain("частный IP-адрес; адрес скрыт");
        report.ShouldContain("Частный DNS выбран: включено");
        report.ShouldContain("Выбранный частный DNS может использовать разрешенный прямой путь в локальную сеть");
        report.ShouldContain("режим прокси не перехватывает DNS всей системы");
        report.ShouldContain("включенных прямых исключений приложений: 0; прямых правил сайтов: 0");
        report.ShouldNotContain("192.168.50.53");
        report.ShouldNotContain("private.example.com");
    }

    [AvaloniaTheory]
    [InlineData(ConnectionState.Off, false, CheckState.Off, "не применена")]
    [InlineData(ConnectionState.Connecting, false, CheckState.Waiting, "ожидает подключения")]
    [InlineData(ConnectionState.Connected, true, CheckState.Warning, "не подтверждена или требует внимания")]
    public void Inactive_or_paused_proxy_does_not_replace_the_policy_snapshot_with_an_active_proxy_label(
        ConnectionState state, bool paused, CheckState policy, string expected)
    {
        var status = Connected(ConnectionMode.Proxy, policy) with
        {
            State = state,
            CaptivePauseUntil = paused ? Time.GetUtcNow().AddMinutes(5) : null,
        };

        var report = string.Join('\n', DnsReport.Lines(AppSettings.Default with { Mode = ConnectionMode.Proxy },
            status, ServiceKind.User, null, null));

        report.ShouldContain("DNS-защита по снимку службы: " + expected);
        report.ShouldNotContain("частичная: режим прокси");
    }

    [AvaloniaTheory]
    [InlineData("https://dns-user:dns-password@203.0.113.53/opaque-config-id-xy123?auth=private-query-token#private-fragment")]
    [InlineData("https://dns-user:dns-password@tenant-secret.dns.example.com/opaque-config-id-xy123?auth=private-query-token#private-fragment")]
    public void Build_report_includes_dns_summary_but_never_custom_doh_credentials_or_opaque_configuration_id(string custom)
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Checks = [new DiagnosticResult(DiagnosticCheck.Dns, CheckState.Warning, "dns_no_answer")],
        });
        Service<SettingsService>().Update(settings => settings with
        {
            Protection = settings.Protection with { Dns = DnsChoice.Custom, CustomDns = custom },
        });
        service.ShouldNotBeNull().Push(status => status with
        {
            State = ConnectionState.Connected,
            Protection = ProtectionChecks.For(ConnectionState.Connected),
        });
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !page.IsRunning);

        var report = page.BuildReport(["DNS адаптера: 203.0.113.53"]);

        report.ShouldContain("DNS: настройки, защита и проверка");
        report.ShouldContain("пользовательский DoH; адрес скрыт");
        report.ShouldContain("DNS адаптера: [адрес-");
        foreach (var secret in new[] { custom, "dns-user", "dns-password", new Uri(custom).Host, "203.0.113.53", "opaque-config-id-xy123", "private-query-token", "private-fragment" })
        {
            report.ShouldNotContain(secret);
        }
    }

    [AvaloniaFact]
    public void English_report_and_adapter_scope_are_readable_without_revealing_resolver_addresses()
    {
        Loc.Instance.SetLanguage("en");
        var report = string.Join('\n', DnsReport.Lines(AppSettings.Default, Connected(ConnectionMode.Vpn, CheckState.Ok),
            ServiceKind.Installed, null, null));
        var inspector = string.Join('\n', InspectorReport.Lines(null, null, null, false, null));

        report.ShouldContain("Default: Cloudflare");
        report.ShouldContain("IPv4 UDP/53");
        report.ShouldContain("DoH/DoT");
        inspector.ShouldContain("OS/TUN");
        inspector.ShouldContain("browser");
    }

    private ConnectionStatus Connected(ConnectionMode mode, CheckState dns) => ConnectionStatus.Initial(Time.GetUtcNow()) with
    {
        State = ConnectionState.Connected,
        Mode = mode,
        Protection = ProtectionChecks.For(ConnectionState.Connected, mode) with { Dns = dns },
    };
}
