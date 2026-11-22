using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Только память службы: не журнал, не файл и не экспорт диагностики. Разрешенные обращения не сохраняются.
/// Дата — местная дата устройства; переход суток и выход из аккаунта очищают адреса.
/// </summary>
public sealed partial class BlockedSiteHistory(TimeProvider time)
{
    public const int Limit = 250;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, BlockedSite> _sites = new(StringComparer.Ordinal);
    private DateOnly _day = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>
    /// Перехватывает выбор маршрута закрепленной версии Xray до общего журнала. Dispatcher пишет destination уже после
    /// FakeDNS и SNI; access.To, напротив, оставляет исходный IP. Разрешенные маршруты и любой access отбрасываются.
    /// </summary>
    public bool Consume(string line, Func<bool>? mayRecord = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains(" accepted ", StringComparison.Ordinal) || line.Contains(" rejected ", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Length > 4096)
        {
            return false;
        }
        var route = DispatcherRoute().Match(line);
        if (!route.Success)
        {
            return false;
        }
        if (route.Groups["outbound"].Value != "block")
        {
            return true;
        }
        var target = route.Groups["target"].Value;
        if (!target.StartsWith("tcp:", StringComparison.Ordinal) && !target.StartsWith("udp:", StringComparison.Ordinal))
        {
            return true;
        }

        var endpoint = target[4..];
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return true;
        }

        var host = endpoint[..colon].Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length > 253 || SitePattern.Normalize(host) is not { } normalized || normalized != host
            || host.Contains('/', StringComparison.Ordinal) || host.StartsWith("geosite:", StringComparison.Ordinal)
            || host.StartsWith("*.", StringComparison.Ordinal))
        {
            return true;
        }

        // FakeDNS без восстановленного имени не является адресом сайта: разрешение такого IP вводило бы в заблуждение.
        if (IPAddress.TryParse(host, out var address) && address.GetAddressBytes() is [198, 18 or 19, _, _])
        {
            return true;
        }

        lock (_gate)
        {
            if (mayRecord?.Invoke() == false)
            {
                return true;
            }
            NewDay();
            var count = _sites.TryGetValue(host, out var old) ? Math.Min(old.Count, int.MaxValue - 1) + 1 : 1;
            if (old is null && _sites.Count == Limit)
            {
                _sites.Remove(_sites.Values.MinBy(s => s.LastSeen)!.Host);
            }
            _sites[host] = new BlockedSite(host, count, time.GetUtcNow());
        }

        return true;
    }

    [GeneratedRegex(@"\A(?:\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? )?\[Info\] (?:\[\d+\] )?app/dispatcher: (?:Hit route rule: \[[^\]\r\n]*\] so )?taking detour \[(?<outbound>[^\]\r\n]+)\] for \[(?<target>(?:tcp|udp):[^\r\n]+)\]\s*\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex DispatcherRoute();

    public IReadOnlyList<BlockedSite> Snapshot()
    {
        lock (_gate)
        {
            NewDay();
            return [.. _sites.Values.OrderByDescending(s => s.LastSeen)];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sites.Clear();
        }
    }

    private void NewDay()
    {
        var day = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (day != _day)
        {
            _sites.Clear();
            _day = day;
        }
    }
}
