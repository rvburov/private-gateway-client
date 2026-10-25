using System.Globalization;
using System.Text.Json;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Правила маршрутизации (<c>ip rule</c>) туннеля, оставшиеся после аварии sing-box. При остановке он
/// убирает их сам, а убитый (SIGKILL, нехватка памяти) оставляет: таблица пустеет вместе с адаптером, и
/// IPv4 проходит мимо, но строгие маршруты держат IPv6 без связи — и после отключения сеть не вернулась
/// бы прямой. Номера правил отведены туннелю службы, чужие не трогаются.
/// </summary>
/// <param name="ip">Вызов ip: аргументы → вывод; не вышло — <see cref="IOException"/> с текстом ip.</param>
public sealed class IpRules(Func<IReadOnlyList<string>, string> ip)
{
    /// <summary>ip этой машины (<see cref="IpCommand"/>), <paramref name="netns"/> — внутри сетевого пространства; нет ip — null.</summary>
    public static IpRules? ForThisMachine(string? netns = null) => IpCommand.ForThisMachine(netns) is { } ip ? new IpRules(ip) : null;

    /// <summary>Убрать правила IPv4 и IPv6 с номерами от <paramref name="first"/> до <paramref name="last"/>; сколько убрано.</summary>
    public int Remove(int first, int last)
    {
        var removed = 0;
        foreach (var family in new[] { "-4", "-6" })
        {
            string json;
            try
            {
                json = ip(["-j", family, "rule", "show"]);
            }
            catch (IOException) when (family == "-6")
            {
                // IPv6 в ядре выключен — и правил IPv6 нет.
                continue;
            }

            foreach (var priority in Priorities(json).Where(p => p >= first && p <= last))
            {
                ip([family, "rule", "del", "priority", priority.ToString(CultureInfo.InvariantCulture)]);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Номера правил из <c>ip -j rule show</c>, по одному на правило.</summary>
    public static IEnumerable<int> Priorities(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray()
            .Where(rule => rule.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number)
            .Select(rule => rule.GetProperty("priority").GetInt32())];
    }
}
