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
}
