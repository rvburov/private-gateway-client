using System.Globalization;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Компактный результат ручной проверки; отсутствие замера не означает отказ сервера.</summary>
public static class ServerLatencyText
{
    public static bool IsUnconfirmed(ServerProfile server) => server.LatencyFailure == LatencyFailure.CoreFailed
        && server.LatencyDiagnostic is "ProtectionBlocked" or "ProtectedProbeUnavailable" or "NetworkUnavailable";

    public static string Of(ServerProfile server, bool card = false)
    {
        if (server.IsMeasuringLatency)
            return "…";
        if (card && server.IsLatencyStale)
            return Loc.Instance["latency.short.stale"];
        if (server.LatencyMs is { } ms)
            return ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"];
        if (card)
            return Loc.Instance[server.LatencyFailure is null ? "latency.unchecked" : "card.latency.none"];
        return server.LatencyFailure is not null ? Loc.Instance["servers.latency.unreachable"] : string.Empty;
    }

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
