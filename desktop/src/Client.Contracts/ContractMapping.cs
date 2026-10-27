using CoreAddresses = PrivateGateway.Client.Core.Settings.AddressPreference;
using CoreCheck = PrivateGateway.Client.Core.DiagnosticCheck;
using CoreCheckState = PrivateGateway.Client.Core.CheckState;
using CoreFingerprint = PrivateGateway.Client.Core.Settings.TlsFingerprint;
using CoreLatency = PrivateGateway.Client.Core.Servers.LatencyResult;
using CoreLatencyFailure = PrivateGateway.Client.Core.Servers.LatencyFailure;
using CoreLevel = PrivateGateway.Client.Core.JournalLevel;
using CoreLog = PrivateGateway.Client.Core.Settings.CoreLogLevel;
using CoreProtocol = PrivateGateway.Client.Core.ProxyProtocol;
using CoreReconnect = PrivateGateway.Client.Core.Settings.ReconnectPolicy;
using CoreRouting = PrivateGateway.Client.Core.Settings.RoutingMode;
using CoreRuleAction = PrivateGateway.Client.Core.Settings.RuleAction;
using CoreSecurity = PrivateGateway.Client.Core.TransportSecurity;
using CoreSettings = PrivateGateway.Client.Core.Settings.AppSettings;
using CoreState = PrivateGateway.Client.Core.ConnectionState;
using CoreTun = PrivateGateway.Client.Core.Settings.TunEngine;
using CoreUpdateChannel = PrivateGateway.Client.Core.Settings.UpdateChannel;
using CoreUpdateOffer = PrivateGateway.Client.Core.Updates.UpdateOffer;
using CoreUpdateState = PrivateGateway.Client.Core.Updates.UpdateState;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Перевод перечислений ядра программы в контракт IPC и обратно. Значение UNSPECIFIED из
/// контракта — ошибка отправителя: служба такой запрос отклоняет, а не угадывает.
/// </summary>
public static partial class ContractMapping
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

    public static V1.LatencyFailure ToContract(this CoreLatencyFailure failure) => failure switch
    {
        CoreLatencyFailure.Unsupported => V1.LatencyFailure.Unsupported,
        CoreLatencyFailure.NoResponse => V1.LatencyFailure.NoResponse,
        CoreLatencyFailure.CoreFailed => V1.LatencyFailure.CoreFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    public static CoreLatencyFailure ToCore(this V1.LatencyFailure failure) => failure switch
    {
        V1.LatencyFailure.Unsupported => CoreLatencyFailure.Unsupported,
        V1.LatencyFailure.NoResponse => CoreLatencyFailure.NoResponse,
        V1.LatencyFailure.CoreFailed => CoreLatencyFailure.CoreFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Причина не задана"),
    };

    /// <summary>Результат проверки задержки для ответа MeasureLatency.</summary>
    public static V1.LatencyResult ToContract(this CoreLatency result, string id)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Milliseconds is { } ms
            ? new V1.LatencyResult { Id = id, Reachable = true, LatencyMs = ms }
            : new V1.LatencyResult { Id = id, Reachable = false, Failure = result.Failure!.Value.ToContract() };
    }

    /// <summary>Результат из ответа службы; недоступный без причины — ошибка отправителя.</summary>
    public static CoreLatency ToCore(this V1.LatencyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Reachable ? new CoreLatency(result.LatencyMs, null) : CoreLatency.Failed(result.Failure.ToCore());
    }

    public static V1.UpdateChannel ToContract(this CoreUpdateChannel channel) => channel switch
    {
        CoreUpdateChannel.Stable => V1.UpdateChannel.Stable,
        CoreUpdateChannel.Beta => V1.UpdateChannel.Beta,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    public static CoreUpdateChannel ToCore(this V1.UpdateChannel channel) => channel switch
    {
        V1.UpdateChannel.Stable => CoreUpdateChannel.Stable,
        V1.UpdateChannel.Beta => CoreUpdateChannel.Beta,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Канал не задан"),
    };

    public static V1.UpdateState ToContract(this CoreUpdateState state) => state switch
    {
        CoreUpdateState.NotSigned => V1.UpdateState.NotSigned,
        CoreUpdateState.UpToDate => V1.UpdateState.UpToDate,
        CoreUpdateState.Available => V1.UpdateState.Available,
        CoreUpdateState.Failed => V1.UpdateState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static CoreUpdateState ToCore(this V1.UpdateState state) => state switch
    {
        V1.UpdateState.NotSigned => CoreUpdateState.NotSigned,
        V1.UpdateState.UpToDate => CoreUpdateState.UpToDate,
        V1.UpdateState.Available => CoreUpdateState.Available,
        V1.UpdateState.Failed => CoreUpdateState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Состояние обновления не задано"),
    };

    /// <summary>Ответ CheckForUpdate.</summary>
    public static V1.UpdateOffer ToContract(this CoreUpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var contract = new V1.UpdateOffer
        {
            State = offer.State.ToContract(),
            Version = offer.Version ?? string.Empty,
            NotesUrl = offer.Notes?.AbsoluteUri ?? string.Empty,
            CheckedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(offer.Checked),
            Installable = offer.Installable,
            Rollback = offer.Rollback ?? string.Empty,
            ReturnTo = offer.ReturnTo ?? string.Empty,
        };
        contract.Highlights.AddRange(offer.Highlights ?? []);
        return contract;
    }

    /// <summary>Ответ службы; «есть обновление» без версии — ошибка отправителя; версия отката не по SemVer — отката нет.</summary>
    public static CoreUpdateOffer ToCore(this V1.UpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var state = offer.State.ToCore();
        if (state == CoreUpdateState.Available && !PrivateGateway.Client.Core.SemVer.IsValid(offer.Version))
        {
            throw new ArgumentException("Обновление без версии: " + offer.Version, nameof(offer));
        }

        return new CoreUpdateOffer(state, offer.CheckedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            state == CoreUpdateState.Available ? offer.Version : null, [.. offer.Highlights],
            Uri.TryCreate(offer.NotesUrl, UriKind.Absolute, out var notes) && notes.Scheme == Uri.UriSchemeHttps ? notes : null)
        {
            Installable = state == CoreUpdateState.Available && offer.Installable,
            Rollback = PrivateGateway.Client.Core.SemVer.IsValid(offer.Rollback) ? offer.Rollback : null,
            ReturnTo = state == CoreUpdateState.Available && PrivateGateway.Client.Core.SemVer.IsValid(offer.ReturnTo) ? offer.ReturnTo : null,
        };
    }

    public static V1.UpdateDownload ToContract(this PrivateGateway.Client.Core.Updates.UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new V1.UpdateDownload { Received = progress.Received, Total = progress.Total, Ready = progress.Ready, Error = progress.Error ?? string.Empty };
    }

    public static PrivateGateway.Client.Core.Updates.UpdateProgress ToCore(this V1.UpdateDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        return new(download.Received, download.Total, download.Ready, download.Error.Length > 0 ? download.Error : null);
    }

    /// <summary>Что служба говорит о себе — для «О программе» и сверки версий с окном.</summary>
    public static PrivateGateway.Client.Core.ServiceDetails ToCore(this V1.ServiceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new(info.ServiceVersion, info.ApiVersion, [.. info.Cores.Select(c => new PrivateGateway.Client.Core.CoreDetails(c.Name, c.Version, c.Available))],
            info.ServiceBuild);
    }

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
            SystemProxy = c.SystemProxy,
        };
    }

    public static V1.RoutingMode ToContract(this CoreRouting mode) => mode switch
    {
        CoreRouting.AllTraffic => V1.RoutingMode.AllTraffic,
        CoreRouting.Split => V1.RoutingMode.Split,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static CoreRouting ToCore(this V1.RoutingMode mode) => mode switch
    {
        V1.RoutingMode.AllTraffic => CoreRouting.AllTraffic,
        V1.RoutingMode.Split => CoreRouting.Split,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Режим маршрутизации не задан"),
    };

    public static V1.RuleAction ToContract(this CoreRuleAction action) => action switch
    {
        CoreRuleAction.Direct => V1.RuleAction.Direct,
        CoreRuleAction.Proxy => V1.RuleAction.Proxy,
        CoreRuleAction.Block => V1.RuleAction.Block,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public static CoreRuleAction ToCore(this V1.RuleAction action) => action switch
    {
        V1.RuleAction.Direct => CoreRuleAction.Direct,
        V1.RuleAction.Proxy => CoreRuleAction.Proxy,
        V1.RuleAction.Block => CoreRuleAction.Block,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Действие правила не задано"),
    };

    /// <summary>
    /// «Маршрутизация» для службы. Мимо VPN — только в раздельном режиме: программы (включенные) и
    /// правила «Напрямую». В режиме «Весь трафик» остаются правила «Блокировать» и «Через VPN» —
    /// они защиту не ослабляют.
    /// </summary>
    public static V1.RoutingSettings ToRouting(this CoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var r = settings.Routing;
        var split = r.Mode == CoreRouting.Split;
        var routing = new V1.RoutingSettings { Mode = r.Mode.ToContract() };
        if (split)
        {
            routing.BypassApps.AddRange(r.Apps.Where(a => a.IsEnabled).Select(a => a.Path));
        }

        routing.Rules.AddRange(r.Rules
            .Where(rule => split || rule.Action != CoreRuleAction.Direct)
            .Select(rule => new V1.DomainRule { Pattern = rule.Pattern, Action = rule.Action.ToContract() }));
        return routing;
    }
}
