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

/// <summary>Хвост журнала, в котором ошибки и предупреждения держатся дольше обычных записей.</summary>
public static class JournalTail
{
    /// <summary>Ошибка или предупреждение.</summary>
    public static bool IsProblem(this JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Level is JournalLevel.Warning or JournalLevel.Error;
    }

    /// <summary>
    /// Не больше <paramref name="count"/> записей из <paramref name="entries"/>, от старых к новым, — как в журнале на
    /// <paramref name="count"/> записей, где лишняя уходит самая старая обычная: последние ошибки и предупреждения, а на
    /// оставшиеся места — последние обычные записи. Ошибку подключения не вытесняют ни подробный журнал ядра, ни запуск окна
    /// после долгой работы службы, ни перезапуск службы (журнал на диске).
    /// </summary>
    public static IReadOnlyList<JournalEntry> Take(IReadOnlyList<JournalEntry> entries, int count)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var problems = entries.Count(e => e.IsProblem());
        var keepProblems = Math.Min(problems, Math.Max(0, count));
        var keepNormal = Math.Max(0, count - problems);
        var kept = new List<JournalEntry>(keepProblems + keepNormal);
        for (var i = entries.Count - 1; i >= 0 && (keepProblems > 0 || keepNormal > 0); i--)
        {
            if (entries[i].IsProblem() ? keepProblems-- > 0 : keepNormal-- > 0)
            {
                kept.Add(entries[i]);
            }
        }

        kept.Reverse();
        return kept;
    }
}

/// <summary>
/// «Задержка» на «Проверке» (решение 509), медианы трех замеров, мс: <paramref name="ToServerMs"/> — прямое TCP-соединение с
/// сервером (один круг), <paramref name="ThroughServerMs"/> — запрос через сервер по открытому соединению (его показывает
/// «Пинг» на главной), <paramref name="NewConnectionMs"/> — тот же запрос по новому соединению. Не измерено — null.
/// </summary>
public sealed record DelayBreakdown(int? ToServerMs, int? ThroughServerMs, int? NewConnectionMs, DateTimeOffset MeasuredAt)
{
    /// <summary>Сколько кругов до сервера уходит на новое соединение — по замерам; не измерено — null.</summary>
    public double? Rounds => ToServerMs is { } one && one > 0 && NewConnectionMs is { } fresh ? Math.Round((double)fresh / one, 1) : null;
}

