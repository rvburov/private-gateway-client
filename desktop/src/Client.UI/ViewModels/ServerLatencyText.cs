using System.Globalization;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Отсутствие замера не означает отказ сервера. Одна подпись для списка и карточки.</summary>
public static class ServerLatencyText
{
    public static bool IsUnconfirmed(ServerProfile server) => server.LatencyFailure == LatencyFailure.CoreFailed
        && server.LatencyDiagnostic is "ProtectionBlocked" or "ProtectedProbeUnavailable" or "NetworkUnavailable";

    public static string Of(ServerProfile server, bool card = false) => server.IsMeasuringLatency ? "…"
        : server.IsLatencyStale ? Loc.Instance["latency.short.stale"]
        : server.LatencyMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"]
        : server.LatencyFailure is not null ? Loc.Instance[card ? "card.latency.none" : FailureLabel(server)]
        : card ? Loc.Instance["latency.unchecked"] : "—";

    private static string FailureLabel(ServerProfile server) => server.LatencyFailure switch
    {
        LatencyFailure.Unsupported => "latency.short.unsupported",
        LatencyFailure.CoreFailed when server.LatencyDiagnostic == "ProtectionBlocked" => "latency.short.blocked",
        LatencyFailure.CoreFailed when server.LatencyDiagnostic == "ProtectedProbeUnavailable" => "latency.short.protectedProbeUnavailable",
        LatencyFailure.CoreFailed when server.LatencyDiagnostic == "NetworkUnavailable" => "latency.short.networkUnavailable",
        LatencyFailure.CoreFailed => "latency.short.coreFailed",
        LatencyFailure.NoResponse => server.LatencyDiagnostic switch
        {
            "Timeout" => "latency.short.timeout",
            "NameResolution" => "latency.short.dns",
            "Tls" => "latency.short.tls",
            "Connection" or "Proxy" => "latency.short.connection",
            _ => "servers.latency.none",
        },
        _ => "servers.latency.none",
    };

    public static string? Reason(ServerProfile server) => server.IsLatencyStale ? Loc.Instance["latency.stale"]
        : server.LatencyFailure switch
        {
            LatencyFailure.Unsupported => Loc.Instance["latency.unsupported"],
            LatencyFailure.CoreFailed when server.LatencyDiagnostic == "ProtectionBlocked" => Loc.Instance["latency.blocked"],
            LatencyFailure.CoreFailed when server.LatencyDiagnostic == "ProtectedProbeUnavailable" => Loc.Instance["latency.protectedProbeUnavailable"],
            LatencyFailure.CoreFailed when server.LatencyDiagnostic == "NetworkUnavailable" => Loc.Instance["latency.networkUnavailable"],
            LatencyFailure.CoreFailed => Loc.Instance["latency.coreFailed"],
            LatencyFailure.NoResponse => Loc.Instance[server.LatencyDiagnostic switch
            {
                "Timeout" => "latency.timeout",
                "NameResolution" => "latency.dns",
                "Tls" => "latency.tls",
                "Connection" or "Proxy" => "latency.connection",
                _ => "latency.noResponse",
            }],
            _ => server.LatencyRoute == "vpn" ? Loc.Instance["latency.viaVpn"] : null,
        };
}
