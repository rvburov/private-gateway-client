using CoreCheck = PrivateGateway.Client.Core.DiagnosticCheck;
using CoreCheckState = PrivateGateway.Client.Core.CheckState;
using CoreLevel = PrivateGateway.Client.Core.JournalLevel;
using CoreState = PrivateGateway.Client.Core.ConnectionState;
using CoreAddresses = PrivateGateway.Client.Core.Settings.AddressPreference;
using CoreFingerprint = PrivateGateway.Client.Core.Settings.TlsFingerprint;
using CoreLog = PrivateGateway.Client.Core.Settings.CoreLogLevel;
using CoreProtocol = PrivateGateway.Client.Core.ProxyProtocol;
using CoreReconnect = PrivateGateway.Client.Core.Settings.ReconnectPolicy;
using CoreSecurity = PrivateGateway.Client.Core.TransportSecurity;
using CoreSettings = PrivateGateway.Client.Core.Settings.AppSettings;
using CoreTun = PrivateGateway.Client.Core.Settings.TunEngine;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Перевод перечислений ядра программы в контракт IPC и обратно. Значение UNSPECIFIED из
/// контракта — ошибка отправителя: служба такой запрос отклоняет, а не угадывает.
/// </summary>
public static class ContractMapping
{
    /// <summary>Текущая версия контракта, ее служба отдает в ServiceInfo.api_version.</summary>
    public const string ApiVersion = "1";

    public static V1.ConnectionState ToContract(this CoreState state) => state switch
    {
        CoreState.Off => V1.ConnectionState.Off,
        CoreState.Connecting => V1.ConnectionState.Connecting,
        CoreState.Connected => V1.ConnectionState.Connected,
        CoreState.Reconnecting => V1.ConnectionState.Reconnecting,
        CoreState.Blocked => V1.ConnectionState.Blocked,
        CoreState.Error => V1.ConnectionState.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static CoreState ToCore(this V1.ConnectionState state) => state switch
    {
        V1.ConnectionState.Off => CoreState.Off,
        V1.ConnectionState.Connecting => CoreState.Connecting,
        V1.ConnectionState.Connected => CoreState.Connected,
        V1.ConnectionState.Reconnecting => CoreState.Reconnecting,
        V1.ConnectionState.Blocked => CoreState.Blocked,
        V1.ConnectionState.Error => CoreState.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Состояние не задано"),
    };

    public static V1.Protocol ToContract(this CoreProtocol protocol) => protocol switch
    {
        CoreProtocol.Vless => V1.Protocol.Vless,
        CoreProtocol.Vmess => V1.Protocol.Vmess,
        CoreProtocol.Trojan => V1.Protocol.Trojan,
        CoreProtocol.Shadowsocks => V1.Protocol.Shadowsocks,
        CoreProtocol.Hysteria2 => V1.Protocol.Hysteria2,
        CoreProtocol.Tuic => V1.Protocol.Tuic,
        CoreProtocol.WireGuard => V1.Protocol.Wireguard,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    public static CoreProtocol ToCore(this V1.Protocol protocol) => protocol switch
    {
        V1.Protocol.Vless => CoreProtocol.Vless,
        V1.Protocol.Vmess => CoreProtocol.Vmess,
        V1.Protocol.Trojan => CoreProtocol.Trojan,
        V1.Protocol.Shadowsocks => CoreProtocol.Shadowsocks,
        V1.Protocol.Hysteria2 => CoreProtocol.Hysteria2,
        V1.Protocol.Tuic => CoreProtocol.Tuic,
        V1.Protocol.Wireguard => CoreProtocol.WireGuard,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Протокол не задан"),
    };

    public static V1.SecurityType ToContract(this CoreSecurity security) => security switch
    {
        CoreSecurity.None => V1.SecurityType.None,
        CoreSecurity.Tls => V1.SecurityType.Tls,
        CoreSecurity.Reality => V1.SecurityType.Reality,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };

    public static CoreSecurity ToCore(this V1.SecurityType security) => security switch
    {
        V1.SecurityType.None => CoreSecurity.None,
        V1.SecurityType.Tls => CoreSecurity.Tls,
        V1.SecurityType.Reality => CoreSecurity.Reality,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Защита транспорта не задана"),
    };

    public static V1.CheckState ToContract(this CoreCheckState state) => state switch
    {
        CoreCheckState.Ok => V1.CheckState.Ok,
        CoreCheckState.Waiting => V1.CheckState.Waiting,
        CoreCheckState.Stopped => V1.CheckState.Stopped,
        CoreCheckState.Off => V1.CheckState.Off,
        CoreCheckState.Warning => V1.CheckState.Warning,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static CoreCheckState ToCore(this V1.CheckState state) => state switch
    {
        V1.CheckState.Ok => CoreCheckState.Ok,
        V1.CheckState.Waiting => CoreCheckState.Waiting,
        V1.CheckState.Stopped => CoreCheckState.Stopped,
        V1.CheckState.Off => CoreCheckState.Off,
        V1.CheckState.Warning => CoreCheckState.Warning,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Состояние проверки не задано"),
    };

    public static V1.LogLevel ToContract(this CoreLevel level) => level switch
    {
        CoreLevel.Debug => V1.LogLevel.Debug,
        CoreLevel.Info => V1.LogLevel.Info,
        CoreLevel.Warning => V1.LogLevel.Warning,
        CoreLevel.Error => V1.LogLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static CoreLevel ToCore(this V1.LogLevel level) => level switch
    {
        V1.LogLevel.Debug => CoreLevel.Debug,
        V1.LogLevel.Info => CoreLevel.Info,
        V1.LogLevel.Warning => CoreLevel.Warning,
        V1.LogLevel.Error => CoreLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Уровень записи не задан"),
    };

    /// <summary>Идентификатор проверки в CheckResult.id.</summary>
    public static string ToContractId(this CoreCheck check) => check switch
    {
        CoreCheck.Service => "service",
        CoreCheck.Core => "core",
        CoreCheck.AllTraffic => "all_traffic",
        CoreCheck.KillSwitch => "kill_switch",
        CoreCheck.Dns => "dns",
        CoreCheck.Ipv6 => "ipv6",
        CoreCheck.ExitIp => "exit_ip",
        CoreCheck.OtherVpn => "other_vpn",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };

    /// <summary>Проверка по CheckResult.id; неизвестная (служба новее интерфейса) — null, а не ошибка.</summary>
    public static CoreCheck? ToCoreCheck(string id) => id switch
    {
        "service" => CoreCheck.Service,
        "core" => CoreCheck.Core,
        "all_traffic" => CoreCheck.AllTraffic,
        "kill_switch" => CoreCheck.KillSwitch,
        "dns" => CoreCheck.Dns,
        "ipv6" => CoreCheck.Ipv6,
        "exit_ip" => CoreCheck.ExitIp,
        "other_vpn" => CoreCheck.OtherVpn,
        _ => null,
    };

    public static V1.AddressPreference ToContract(this CoreAddresses preference) => preference switch
    {
        CoreAddresses.PreferIpv4 => V1.AddressPreference.PreferIpv4,
        CoreAddresses.PreferIpv6 => V1.AddressPreference.PreferIpv6,
        CoreAddresses.Ipv4Only => V1.AddressPreference.Ipv4Only,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null),
    };

    public static CoreAddresses ToCore(this V1.AddressPreference preference) => preference switch
    {
        V1.AddressPreference.PreferIpv4 => CoreAddresses.PreferIpv4,
        V1.AddressPreference.PreferIpv6 => CoreAddresses.PreferIpv6,
        V1.AddressPreference.Ipv4Only => CoreAddresses.Ipv4Only,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Приоритет адресов не задан"),
    };

    /// <summary>
    /// Защита для службы (ApplyProtection): блок при обрыве — из «Основных», остальное — из «Защиты
    /// и утечек». Обнаружение устройств без локальной сети не включается.
    /// </summary>
    public static V1.ProtectionSettings ToContract(this CoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var p = settings.Protection;
        var dns = new V1.DnsSettings { BlockOutsideDns = p.BlockOutsideDns, FakeDns = p.FakeDns };
        dns.Servers.AddRange(p.DnsServers);
        return new V1.ProtectionSettings
        {
            KillSwitch = settings.KillSwitch,
            BlockIpv6 = p.BlockIpv6,
            AllowLan = p.AllowLan,
            Dns = dns,
            BootProtection = p.BootProtection,
            StrictMode = p.StrictMode && settings.KillSwitch,
            LanDiscovery = p.LanDiscovery && p.AllowLan,
            AddressPreference = p.Addresses.ToContract(),
            CheckLeaksAfterConnect = p.CheckLeaksAfterConnect,
        };
    }

    public static V1.CoreLogLevel ToContract(this CoreLog level) => level switch
    {
        CoreLog.Warning => V1.CoreLogLevel.Warning,
        CoreLog.Info => V1.CoreLogLevel.Info,
        CoreLog.Debug => V1.CoreLogLevel.Debug,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static CoreLog ToCore(this V1.CoreLogLevel level) => level switch
    {
        V1.CoreLogLevel.Warning => CoreLog.Warning,
        V1.CoreLogLevel.Info => CoreLog.Info,
        V1.CoreLogLevel.Debug => CoreLog.Debug,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Подробность журнала ядра не задана"),
    };

    public static V1.TunEngine ToContract(this CoreTun engine) => engine switch
    {
        CoreTun.System => V1.TunEngine.System,
        CoreTun.Gvisor => V1.TunEngine.Gvisor,
        CoreTun.Mixed => V1.TunEngine.Mixed,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
    };

    public static CoreTun ToCore(this V1.TunEngine engine) => engine switch
    {
        V1.TunEngine.System => CoreTun.System,
        V1.TunEngine.Gvisor => CoreTun.Gvisor,
        V1.TunEngine.Mixed => CoreTun.Mixed,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Стек TUN не задан"),
    };

    public static V1.TlsFingerprint ToContract(this CoreFingerprint fingerprint) => fingerprint switch
    {
        CoreFingerprint.Profile => V1.TlsFingerprint.Profile,
        CoreFingerprint.Chrome => V1.TlsFingerprint.Chrome,
        CoreFingerprint.Firefox => V1.TlsFingerprint.Firefox,
        CoreFingerprint.Safari => V1.TlsFingerprint.Safari,
        CoreFingerprint.Random => V1.TlsFingerprint.Random,
        _ => throw new ArgumentOutOfRangeException(nameof(fingerprint), fingerprint, null),
    };

    public static CoreFingerprint ToCore(this V1.TlsFingerprint fingerprint) => fingerprint switch
    {
        V1.TlsFingerprint.Profile => CoreFingerprint.Profile,
        V1.TlsFingerprint.Chrome => CoreFingerprint.Chrome,
        V1.TlsFingerprint.Firefox => CoreFingerprint.Firefox,
        V1.TlsFingerprint.Safari => CoreFingerprint.Safari,
        V1.TlsFingerprint.Random => CoreFingerprint.Random,
        _ => throw new ArgumentOutOfRangeException(nameof(fingerprint), fingerprint, "Отпечаток TLS не задан"),
    };

    public static V1.ReconnectPolicy ToContract(this CoreReconnect policy) => policy switch
    {
        CoreReconnect.Backoff60Seconds => V1.ReconnectPolicy.Backoff60S,
        CoreReconnect.UpTo5Minutes => V1.ReconnectPolicy.UpTo5Minutes,
        CoreReconnect.Never => V1.ReconnectPolicy.Never,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static CoreReconnect ToCore(this V1.ReconnectPolicy policy) => policy switch
    {
        V1.ReconnectPolicy.Backoff60S => CoreReconnect.Backoff60Seconds,
        V1.ReconnectPolicy.UpTo5Minutes => CoreReconnect.UpTo5Minutes,
        V1.ReconnectPolicy.Never => CoreReconnect.Never,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Переподключение не задано"),
    };

    /// <summary>«Подключение и ядро» для службы — в запросе Connect вместе с защитой.</summary>
    public static V1.ConnectionSettings ToConnection(this CoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var c = settings.Connection;
        return new V1.ConnectionSettings
        {
            CoreLog = c.CoreLog.ToContract(),
            TunEngine = c.Stack.ToContract(),
            Mtu = (uint)(c.Mtu ?? 0),
            StrictRoutes = c.StrictRoutes,
            Sniffing = c.Sniffing,
            Fingerprint = c.Fingerprint.ToContract(),
            Mux = c.Mux,
            TlsFragment = c.TlsFragment,
            ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(c.ConnectTimeoutSeconds)),
            Reconnect = c.Reconnect.ToContract(),
            ProxyPort = (uint)c.ProxyPort,
            ProxyAllowLan = c.ProxyAllowLan,
        };
    }
}
