namespace PrivateGateway.Client.Core;

/// <summary>Проверки экрана «Проверка» в том порядке, в каком они показаны (docs/design, доска 7).</summary>
public enum DiagnosticCheck
{
    /// <summary>Служба работает, канал связи с интерфейсом защищен.</summary>
    Service,

    /// <summary>Сетевое ядро (Xray-core или sing-box) запущено с конфигурацией службы.</summary>
    Core,

    /// <summary>Весь трафик идет в туннель: TUN-адаптер и маршрут по умолчанию.</summary>
    AllTraffic,

    /// <summary>Блок при обрыве: правила брандмауэра ОС.</summary>
    KillSwitch,

    Dns,

    Ipv6,

    /// <summary>Какой IP видят сайты.</summary>
    ExitIp,

    /// <summary>Другие VPN и системные прокси, которые могут увести трафик мимо туннеля.</summary>
    OtherVpn,
}

/// <summary>
/// Результат одной проверки. <paramref name="Reason"/> — код уточнения для текста интерфейса
/// (например, <c>system_proxy</c>); null — текст по состоянию.
/// </summary>
public sealed record DiagnosticResult(DiagnosticCheck Check, CheckState State, string? Reason = null);

/// <summary>Отчет службы о проверках. Проверки только читают состояние и ничего не меняют.</summary>
public sealed record DiagnosticReport(IReadOnlyList<DiagnosticResult> Results, DateTimeOffset FinishedAt)
{
    public DiagnosticResult? this[DiagnosticCheck check] => Results.FirstOrDefault(r => r.Check == check);
}

/// <summary>Уровень записи журнала службы.</summary>
public enum JournalLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Запись журнала службы для экрана «Проверка». Текст уже пропущен службой через маскирование
/// секретов; интерфейс маскирует его еще раз перед показом и в отчете.
/// </summary>
/// <param name="Time">Когда записано.</param>
/// <param name="Level">Уровень записи.</param>
/// <param name="Source">Кто пишет: service, xray, sing-box, firewall, dns…</param>
/// <param name="Message">Текст записи, секреты скрыты.</param>
public sealed record JournalEntry(DateTimeOffset Time, JournalLevel Level, string Source, string Message);
