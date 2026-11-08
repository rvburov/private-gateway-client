using System.Globalization;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Отсутствие замера не означает отказ сервера. Одна подпись для списка и карточки.</summary>
public static class ServerLatencyText
{
    public static string Of(ServerProfile server, bool card = false) => server.IsMeasuringLatency ? "…"
        : server.LatencyMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"]
        : server.LatencyFailure is not null ? Loc.Instance[card ? "card.latency.none" : "servers.latency.none"]
        : card ? Loc.Instance["latency.unchecked"] : "—";

    public static string? Reason(ServerProfile server) => server.LatencyFailure switch
    {
        LatencyFailure.Unsupported => Loc.Instance["latency.unsupported"],
        LatencyFailure.CoreFailed when server.LatencyDiagnostic == "ProtectionBlocked" => Loc.Instance["latency.blocked"],
        LatencyFailure.CoreFailed => Loc.Instance["latency.coreFailed"],
        LatencyFailure.NoResponse => Loc.Instance[server.LatencyDiagnostic switch
        {
            "Timeout" => "latency.timeout",
            "NameResolution" => "latency.dns",
            "Tls" => "latency.tls",
            "Connection" or "Proxy" => "latency.connection",
            _ => "latency.noResponse",
        }],
        _ => null,
    };
}
