using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Уровень записи журнала для строки, которую печатает туннель (<see cref="VpnTunnel"/>) или ядро. Заметки самой службы о
/// запуске туннеля (повтор, адрес TUN занят, сняты оставшиеся адреса, IPv6 закрыт) — предупреждения. Строки, после которых
/// ядро выходит (sing-box FATAL и PANIC, Xray «Failed to start»), — ошибки: в отчете они в разделе «Ошибки и
/// предупреждения», и отказ первой попытки виден с причиной (у пользователя на Windows 2026-10-31 причина была только среди
/// обычных строк). Остальное — обычные записи: ошибки отдельных соединений sing-box пишет уровнем ERROR, Xray запуск —
/// уровнем Warning, и раздел ошибок утонул бы в них.
/// </summary>
public static class CoreLines
{
    public static JournalLevel Level(string source, string line)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(line);
        return source switch
        {
            "service" => JournalLevel.Warning,
            "sing-box" when line.StartsWith("FATAL", StringComparison.Ordinal) || line.StartsWith("PANIC", StringComparison.Ordinal)
                || line.StartsWith("panic:", StringComparison.Ordinal) => JournalLevel.Error,
            "xray" when line.StartsWith("Failed to start", StringComparison.Ordinal) || line.StartsWith("panic:", StringComparison.Ordinal)
                => JournalLevel.Error,
            _ => JournalLevel.Info,
        };
    }

    /// <summary>
    /// Последняя с <paramref name="since"/> строка Xray об отказе выхода к серверу — причина словами самого ядра, без отметки
    /// времени, не длиннее <see cref="ReasonLength"/>; нет — null. Строки DNS не в счет: при неработающем выходе DNS через сервер
    /// тоже не отвечает, и это следствие, а не причина (отчет пользователя 2026-11-06 18:46: в журнале «Отладка» конец занят
    /// ими, а отказ сервера остался выше и в отчет не попал).
    /// </summary>
    public static string? OutboundFailure(IEnumerable<JournalEntry> entries, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var line = entries.LastOrDefault(e => e.Source == "xray" && e.Time >= since && IsOutboundFailure(e.Message))?.Message;
        if (line is null)
        {
            return null;
        }

        // «2026/11/06 18:46:14.289512 [Warning] …» — с уровня.
        var level = line.IndexOf('[', StringComparison.Ordinal);
        var reason = level > 0 ? line[level..] : line;
        return reason.Length > ReasonLength ? reason[..ReasonLength] + "…" : reason;
    }

    /// <summary>Сколько знаков причины ядра идет в вывод.</summary>
    public const int ReasonLength = 300;

    private static bool IsOutboundFailure(string line) =>
        !line.Contains("app/dns", StringComparison.Ordinal) && !line.Contains("proxy/dns", StringComparison.Ordinal)
        && (line.Contains("failed to process outbound traffic", StringComparison.Ordinal)
            || line.Contains("/outbound:", StringComparison.Ordinal) && (line.Contains("failed", StringComparison.Ordinal)
                || line.Contains("EOF", StringComparison.Ordinal) || line.Contains("reset", StringComparison.Ordinal))
            || line.Contains("reality", StringComparison.OrdinalIgnoreCase) && (line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("invalid", StringComparison.OrdinalIgnoreCase) || line.Contains("verif", StringComparison.OrdinalIgnoreCase)));
}
