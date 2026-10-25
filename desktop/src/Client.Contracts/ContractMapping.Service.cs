using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Сервер, настройки и состояние в контракт IPC и обратно: интерфейс собирает ConnectRequest и
/// читает Status, служба — наоборот. Перевод туда и обратно дает то же самое (тест на всех
/// эталонах ссылок); UNSPECIFIED и сервер без данных протокола — ошибка отправителя.
/// </summary>
public static partial class ContractMapping
{
    // ── Перечисления ─────────────────────────────────────────────────────────

    public static V1.Mode ToContract(this ConnectionMode mode) => mode switch
    {
        ConnectionMode.Vpn => V1.Mode.Vpn,
        ConnectionMode.Proxy => V1.Mode.Proxy,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static ConnectionMode ToCore(this V1.Mode mode) => mode switch
    {
        V1.Mode.Vpn => ConnectionMode.Vpn,
        V1.Mode.Proxy => ConnectionMode.Proxy,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Режим не задан"),
    };

    public static V1.TransportType ToContract(this TransportKind kind) => kind switch
    {
        TransportKind.Tcp => V1.TransportType.Tcp,
        TransportKind.WebSocket => V1.TransportType.Websocket,
        TransportKind.Grpc => V1.TransportType.Grpc,
        TransportKind.HttpUpgrade => V1.TransportType.HttpUpgrade,
        TransportKind.Xhttp => V1.TransportType.Xhttp,
        TransportKind.Quic => V1.TransportType.Quic,
        TransportKind.Udp => V1.TransportType.Udp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static TransportKind ToCore(this V1.TransportType type) => type switch
    {
        V1.TransportType.Tcp => TransportKind.Tcp,
        V1.TransportType.Websocket => TransportKind.WebSocket,
        V1.TransportType.Grpc => TransportKind.Grpc,
        V1.TransportType.HttpUpgrade => TransportKind.HttpUpgrade,
        V1.TransportType.Xhttp => TransportKind.Xhttp,
        V1.TransportType.Quic => TransportKind.Quic,
        V1.TransportType.Udp => TransportKind.Udp,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Транспорт не задан"),
    };

    public static V1.NetworkKind ToContract(this NetworkKind kind) => kind switch
    {
        NetworkKind.None => V1.NetworkKind.None,
        NetworkKind.WiFi => V1.NetworkKind.Wifi,
        NetworkKind.Ethernet => V1.NetworkKind.Ethernet,
        NetworkKind.UsbModem => V1.NetworkKind.UsbModem,
        NetworkKind.Mobile => V1.NetworkKind.Mobile,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static NetworkKind ToCore(this V1.NetworkKind kind) => kind switch
    {
        V1.NetworkKind.None => NetworkKind.None,
        V1.NetworkKind.Wifi => NetworkKind.WiFi,
        V1.NetworkKind.Ethernet => NetworkKind.Ethernet,
        V1.NetworkKind.UsbModem => NetworkKind.UsbModem,
        V1.NetworkKind.Mobile => NetworkKind.Mobile,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Сеть не задана"),
    };

    // ── Сервер ───────────────────────────────────────────────────────────────

    /// <summary>Сервер с ключами для ConnectRequest и MeasureLatency; код страны в контракт не идет.</summary>
    public static V1.ServerEndpoint ToContract(this ServerDefinition server, string id)
    {
        ArgumentNullException.ThrowIfNull(server);
        var security = new V1.Security
        {
            Type = server.Security.Kind.ToContract(),
            Sni = server.Security.Sni ?? string.Empty,
            Fingerprint = server.Security.Fingerprint ?? string.Empty,
            AllowInsecure = server.Security.AllowInsecure,
            RealityPublicKey = server.Security.RealityPublicKey ?? string.Empty,
            RealityShortId = server.Security.RealityShortId ?? string.Empty,
            RealitySpiderX = server.Security.RealitySpiderX ?? string.Empty,
        };
        security.Alpn.AddRange(server.Security.Alpn ?? []);
        var endpoint = new V1.ServerEndpoint
        {
            Id = id,
            Name = server.Name,
            Protocol = server.Protocol.ToContract(),
            Host = server.Host,
            Port = (uint)server.Port,
            Transport = new V1.Transport
            {
                Type = server.Transport.Kind.ToContract(),
                Path = server.Transport.Path ?? string.Empty,
                Host = server.Transport.Host ?? string.Empty,
                ServiceName = server.Transport.ServiceName ?? string.Empty,
            },
            Security = security,
        };
        var c = server.Credentials;
        var o = server.Options;
        switch (server.Protocol)
        {
            case ProxyProtocol.Vless:
                endpoint.Vless = new V1.VlessSettings { Uuid = c.Uuid ?? string.Empty, Flow = o.Flow ?? string.Empty };
                break;
            case ProxyProtocol.Vmess:
                endpoint.Vmess = new V1.VmessSettings { Uuid = c.Uuid ?? string.Empty, Cipher = o.Cipher ?? string.Empty };
                break;
            case ProxyProtocol.Trojan:
                endpoint.Trojan = new V1.TrojanSettings { Password = c.Password ?? string.Empty };
                break;
            case ProxyProtocol.Shadowsocks:
                endpoint.Shadowsocks = new V1.ShadowsocksSettings { Method = o.Cipher ?? string.Empty, Password = c.Password ?? string.Empty };
                break;
            case ProxyProtocol.Hysteria2:
                endpoint.Hysteria2 = new V1.Hysteria2Settings { Password = c.Password ?? string.Empty, ObfsPassword = c.ObfsPassword ?? string.Empty };
                break;
            case ProxyProtocol.Tuic:
                endpoint.Tuic = new V1.TuicSettings
                {
                    Uuid = c.Uuid ?? string.Empty,
                    Password = c.Password ?? string.Empty,
                    CongestionControl = o.CongestionControl ?? string.Empty,
                };
                break;
            case ProxyProtocol.WireGuard:
                var wireguard = new V1.WireGuardSettings
                {
                    PrivateKey = c.PrivateKey ?? string.Empty,
                    PeerPublicKey = o.PeerPublicKey ?? string.Empty,
                    PresharedKey = c.PresharedKey ?? string.Empty,
                    Mtu = (uint)(o.Mtu ?? 0),
                };
                wireguard.Addresses.AddRange(o.Addresses ?? []);
                endpoint.Wireguard = wireguard;
                break;
        }

        return endpoint;
    }

    /// <summary>Сервер из запроса интерфейса. Нет данных своего протокола — ошибка отправителя.</summary>
    /// <exception cref="ArgumentException">Протокол или транспорт не заданы, данные другого протокола.</exception>
    public static ServerDefinition ToCore(this V1.ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var protocol = endpoint.Protocol.ToCore();
        var expected = protocol switch
        {
            ProxyProtocol.Vless => V1.ServerEndpoint.SettingsOneofCase.Vless,
            ProxyProtocol.Vmess => V1.ServerEndpoint.SettingsOneofCase.Vmess,
            ProxyProtocol.Trojan => V1.ServerEndpoint.SettingsOneofCase.Trojan,
            ProxyProtocol.Shadowsocks => V1.ServerEndpoint.SettingsOneofCase.Shadowsocks,
            ProxyProtocol.Hysteria2 => V1.ServerEndpoint.SettingsOneofCase.Hysteria2,
            ProxyProtocol.Tuic => V1.ServerEndpoint.SettingsOneofCase.Tuic,
            _ => V1.ServerEndpoint.SettingsOneofCase.Wireguard,
        };
        if (endpoint.SettingsCase != expected)
        {
            throw new ArgumentException("Данные сервера не того протокола", nameof(endpoint));
        }

        var transport = endpoint.Transport ?? throw new ArgumentException("Транспорт не задан", nameof(endpoint));
        var security = endpoint.Security ?? throw new ArgumentException("Защита не задана", nameof(endpoint));
        var options = ProtocolOptions.Empty;
        var credentials = new ServerCredentials();
        switch (endpoint.SettingsCase)
        {
            case V1.ServerEndpoint.SettingsOneofCase.Vless:
                options = options with { Flow = Empty(endpoint.Vless.Flow) };
                credentials = new ServerCredentials(Uuid: Empty(endpoint.Vless.Uuid));
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Vmess:
                options = options with { Cipher = Empty(endpoint.Vmess.Cipher) };
                credentials = new ServerCredentials(Uuid: Empty(endpoint.Vmess.Uuid));
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Trojan:
                credentials = new ServerCredentials(Password: Empty(endpoint.Trojan.Password));
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Shadowsocks:
                options = options with { Cipher = Empty(endpoint.Shadowsocks.Method) };
                credentials = new ServerCredentials(Password: Empty(endpoint.Shadowsocks.Password));
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Hysteria2:
                var obfs = Empty(endpoint.Hysteria2.ObfsPassword);
                // В контракте только пароль обфускации: она бывает одна — salamander.
                options = options with { Obfs = obfs is null ? null : "salamander" };
                credentials = new ServerCredentials(Password: Empty(endpoint.Hysteria2.Password), ObfsPassword: obfs);
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Tuic:
                options = options with { CongestionControl = Empty(endpoint.Tuic.CongestionControl) };
                credentials = new ServerCredentials(Uuid: Empty(endpoint.Tuic.Uuid), Password: Empty(endpoint.Tuic.Password));
                break;
            case V1.ServerEndpoint.SettingsOneofCase.Wireguard:
                var w = endpoint.Wireguard;
                options = options with
                {
                    PeerPublicKey = Empty(w.PeerPublicKey),
                    Addresses = w.Addresses.Count > 0 ? [.. w.Addresses] : null,
                    Mtu = w.Mtu > 0 ? (int)w.Mtu : null,
                };
                credentials = new ServerCredentials(PrivateKey: Empty(w.PrivateKey), PresharedKey: Empty(w.PresharedKey));
                break;
        }

        var kind = transport.Type.ToCore();
        return new ServerDefinition(
            endpoint.Name,
            string.Empty,
            protocol,
            endpoint.Host,
            (int)endpoint.Port,
            kind switch
            {
                TransportKind.Tcp => TransportOptions.Tcp,
                TransportKind.Quic => TransportOptions.Quic,
                TransportKind.Udp => TransportOptions.Udp,
                _ => new TransportOptions(kind, Empty(transport.Path), Empty(transport.Host), Empty(transport.ServiceName)),
            },
            security.Type.ToCore() is var securityKind && securityKind == TransportSecurity.None
                ? SecurityOptions.None
                : new SecurityOptions(
                    securityKind,
                    Empty(security.Sni),
                    Empty(security.Fingerprint),
                    security.Alpn.Count > 0 ? [.. security.Alpn] : null,
                    security.AllowInsecure,
                    Empty(security.RealityPublicKey),
                    Empty(security.RealityShortId),
                    Empty(security.RealitySpiderX)),
            options,
            credentials);
    }

    // ── Настройки для службы ─────────────────────────────────────────────────

    /// <summary>Защита из запроса: блок при обрыве — отдельно, он из «Основных».</summary>
    public static (ProtectionSettings Protection, bool KillSwitch) ToCore(this V1.ProtectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var servers = settings.Dns?.Servers ?? [];
        var (dns, custom) = servers.Count == 0 ? (DnsChoice.Profile, null)
            : servers[0] == "https://1.1.1.1/dns-query" ? (DnsChoice.CloudflareDoh, null)
            : servers[0] == "https://9.9.9.9/dns-query" ? (DnsChoice.Quad9Doh, (string?)null)
            : (DnsChoice.Custom, servers[0]);
        return (new ProtectionSettings(
            settings.BootProtection,
            settings.StrictMode,
            dns,
            custom,
            settings.Dns?.BlockOutsideDns ?? true,
            settings.Dns?.FakeDns ?? false,
            settings.CheckLeaksAfterConnect,
            settings.BlockIpv6,
            settings.AddressPreference.ToCore(),
            settings.AllowLan,
            settings.LanDiscovery), settings.KillSwitch);
    }

    public static ConnectionSettings ToCore(this V1.ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ConnectionSettings(
            settings.CoreLog.ToCore(),
            settings.TunEngine.ToCore(),
            settings.Mtu > 0 ? (int)settings.Mtu : null,
            settings.StrictRoutes,
            settings.Sniffing,
            settings.Fingerprint.ToCore(),
            settings.Mux,
            settings.TlsFragment,
            (int)(settings.ConnectTimeout?.ToTimeSpan().TotalSeconds ?? 10),
            settings.Reconnect.ToCore(),
            settings.ProxyPort is > 0 and <= 65535 ? (int)settings.ProxyPort
                : throw new ArgumentOutOfRangeException(nameof(settings), settings.ProxyPort, "Порт прокси вне 1–65535"),
            settings.ProxyAllowLan,
            settings.SystemProxy);
    }

    /// <summary>Маршрутизация из запроса: у программ в контракте только путь — он же и название.</summary>
    public static RoutingSettings ToCore(this V1.RoutingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RoutingSettings(
            settings.Mode.ToCore(),
            [.. settings.BypassApps.Select(path => new BypassApp(path, path))],
            [.. settings.Rules.Select(r => new DomainRule(r.Pattern, r.Action.ToCore()))]);
    }

    // ── Состояние ────────────────────────────────────────────────────────────

    public static V1.Status ToContract(this ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var p = status.Protection;
        var result = new V1.Status
        {
            State = status.State.ToContract(),
            Mode = status.Mode.ToContract(),
            ServerId = status.ServerId ?? string.Empty,
            Since = Timestamp.FromDateTimeOffset(status.Since),
            Attempt = status.Attempt,
            Protection = new V1.ProtectionStatus
            {
                AllTraffic = p.AllTraffic.ToContract(),
                Dns = p.Dns.ToContract(),
                Ipv6 = p.Ipv6.ToContract(),
                KillSwitch = p.KillSwitch.ToContract(),
                CheckedAt = p.CheckedAt is { } checkedAt ? Timestamp.FromDateTimeOffset(checkedAt) : null,
            },
            Traffic = new V1.TrafficStats
            {
                LatencyMs = status.Traffic.LatencyMs ?? 0,
                DownloadMbps = status.Traffic.DownloadMbps,
                UploadMbps = status.Traffic.UploadMbps,
            },
            Exit = new V1.ExitInfo
            {
                Ip = status.Exit.Ip ?? string.Empty,
                City = status.Exit.City ?? string.Empty,
                NetworkType = status.Exit.NetworkType ?? string.Empty,
                Asn = status.Exit.Asn ?? string.Empty,
                DirectIp = status.Exit.DirectIp ?? string.Empty,
            },
            Network = new V1.NetworkInfo
            {
                Kind = status.Network.Kind.ToContract(),
                Name = status.Network.Name ?? string.Empty,
                IsOpen = status.Network.IsOpen,
                IsMetered = status.Network.IsMetered,
            },
            CaptivePauseUntil = status.CaptivePauseUntil is { } until ? Timestamp.FromDateTimeOffset(until) : null,
        };
        if (status.ErrorCode is { } code)
        {
            result.Error = new V1.ErrorInfo { Code = code };
        }

        return result;
    }

    public static ConnectionStatus ToCore(this V1.Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var p = status.Protection ?? throw new ArgumentException("Нет проверок защиты", nameof(status));
        return new ConnectionStatus(
            status.State.ToCore(),
            status.Mode.ToCore(),
            Empty(status.ServerId),
            status.Since?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            status.Attempt,
            new ProtectionChecks(p.AllTraffic.ToCore(), p.Dns.ToCore(), p.Ipv6.ToCore(), p.KillSwitch.ToCore(), p.CheckedAt?.ToDateTimeOffset()),
            status.Traffic is { } t ? new TrafficStats(t.LatencyMs > 0 ? t.LatencyMs : null, t.DownloadMbps, t.UploadMbps) : TrafficStats.Empty,
            status.Exit is { } e
                ? new ExitInfo(Empty(e.Ip), Empty(e.City), Empty(e.NetworkType), Empty(e.Asn), Empty(e.DirectIp))
                : ExitInfo.Unknown,
            status.Network is { } n ? new NetworkInfo(n.Kind.ToCore(), Empty(n.Name), n.IsOpen, n.IsMetered) : NetworkInfo.Unknown,
            Empty(status.Error?.Code),
            status.CaptivePauseUntil?.ToDateTimeOffset());
    }

    /// <summary>Пустая строка контракта — как отсутствующее значение.</summary>
    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
