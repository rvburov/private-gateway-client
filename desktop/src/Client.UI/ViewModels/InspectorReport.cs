using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Раздел инспектора в отчете «Проверки»: сеть машины (адаптеры и маршруты), до и после VPN (строки с выводом) и сокеты —
/// только сколько их, без адресов. Строки еще не обезличены: это делает отчет целиком.
/// </summary>
public static class InspectorReport
{
    /// <summary>Строки раздела; чего служба не дала — того в отчете нет, а раздел говорит почему.</summary>
    public static IEnumerable<string> Lines(NetworkSnapshot? snapshot, NetworkComparison? comparison, IReadOnlyList<NetworkConnection>? connections,
        bool protectedNow, string? directIp)
    {
        var loc = Loc.Instance;
        yield return loc["diag.report.network"];
        if (snapshot is null)
        {
            yield return "  " + loc["diag.report.none"];
        }
        else
        {
            foreach (var card in snapshot.Cards)
            {
                var adapter = new AdapterCardViewModel(card);
                yield return $"  {card.Name} · {adapter.KindText} · {adapter.StatusText}";
                foreach (var row in adapter.Rows)
                {
                    yield return $"    {row.Label}: {row.Value.Replace('\n', ' ')}";
                }
            }

            yield return "  " + loc["network.routes"];
            foreach (var route in snapshot.Routes)
            {
                var row = new RouteRowViewModel(route);
                yield return row.HasMetric ? $"    {row.Destination} — {row.Via} · {row.Metric}" : $"    {row.Destination} — {row.Via}";
            }
        }

        yield return string.Empty;
        yield return loc["diag.report.compare"];
        if (comparison is null)
        {
            yield return "  " + loc["diag.report.none"];
        }
        else
        {
            foreach (var row in ComparePageViewModel.Compare(comparison, protectedNow, directIp))
            {
                yield return $"  [{row.VerdictText}] {row.Label}: {row.Before} → {row.Now}";
            }
        }

        yield return string.Empty;
        yield return loc["diag.report.sockets"];
        if (connections is null)
        {
            yield return "  " + loc["diag.report.none"];
        }
        else
        {
            var waiting = connections.Count(c => ConnectionsPageViewModel.Waits(c.Socket));
            yield return "  " + loc.Format("diag.report.sockets.count", connections.Count - waiting, waiting,
                connections.Count(c => c.Socket.Protocol == SocketProtocol.Tcp), connections.Count(c => c.Socket.Protocol == SocketProtocol.Udp));
        }
    }
}
