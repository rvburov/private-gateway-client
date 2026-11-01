using System.Globalization;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Системный прокси пользователя окна: «узел:порт» или адрес скрипта автонастройки (<paramref name="IsScript"/>,
/// PAC или WPAD). Браузеры и программы, которые берут прокси из настроек ОС, отдают трафик ему.
/// </summary>
public sealed record UserProxy(string Address, bool IsScript = false)
{
    /// <summary>
    /// Чужой — не подтвержденный вход нашего ядра. Новое окно Windows управляет HKCU пользователя; старые службы
    /// пользователя ставят его сами. Пока фактический порт известен, желаемые настройки его не заменяют.
    /// </summary>
    public bool IsForeign(AppSettings settings, int? ownedPort = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsScript || (ownedPort is > 0
            ? !IsLoopback(ownedPort.Value)
            : !settings.Connection.UsesSystemProxy(settings.Mode) || !IsLoopback(settings.Connection.ProxyPort));
    }

    private bool IsLoopback(int port) => ProxyAddress.Endpoint(Address) == "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Отчет службы с системным прокси пользователя: установленная служба (SYSTEM, root) его не видит,
    /// видит окно. Чужой прокси — предупреждение в «Другие VPN и прокси»; чужой VPN, найденный службой,
    /// важнее и остается.
    /// </summary>
    public static DiagnosticReport WithUserProxy(DiagnosticReport report, UserProxy? proxy, AppSettings settings, int? ownedPort = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (proxy is null || !proxy.IsForeign(settings, ownedPort) || report[DiagnosticCheck.OtherVpn] is { State: CheckState.Warning })
        {
            return report;
        }

        return report with
        {
            Results = [.. report.Results.Where(r => r.Check != DiagnosticCheck.OtherVpn),
                new DiagnosticResult(DiagnosticCheck.OtherVpn, CheckState.Warning, "system_proxy")],
        };
    }
}

/// <summary>Системный прокси пользователя — читает окно: настройки его, а не службы.</summary>
public interface IUserProxySettings
{
    /// <summary>Системный прокси сейчас; выключен или настройки не прочитать — null.</summary>
    UserProxy? Read();
}
