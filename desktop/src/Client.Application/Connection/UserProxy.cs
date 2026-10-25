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
    /// Чужой — не вход нашего ядра: в режиме прокси служба пользователя ставит системный прокси на
    /// 127.0.0.1:порт из «Подключения и ядра»; все остальное поставила другая программа или человек.
    /// </summary>
    public bool IsForeign(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsScript || settings.Mode != ConnectionMode.Proxy || !settings.Connection.SystemProxy
            || Address != "127.0.0.1:" + settings.Connection.ProxyPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Отчет службы с системным прокси пользователя: установленная служба (SYSTEM, root) его не видит,
    /// видит окно. Чужой прокси — предупреждение в «Другие VPN и прокси»; чужой VPN, найденный службой,
    /// важнее и остается.
    /// </summary>
    public static DiagnosticReport WithUserProxy(DiagnosticReport report, UserProxy? proxy, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (proxy is null || !proxy.IsForeign(settings) || report[DiagnosticCheck.OtherVpn] is { State: CheckState.Warning })
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
