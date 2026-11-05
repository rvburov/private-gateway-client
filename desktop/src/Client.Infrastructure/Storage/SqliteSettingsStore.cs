using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>
/// Настройки в таблице settings: ключ → значение. Нет ключа или значение не читается —
/// берется значение по умолчанию (<see cref="AppSettings.Default"/>), а не падает запуск.
/// </summary>
public sealed partial class SqliteSettingsStore(SqliteDatabase database, ILogger<SqliteSettingsStore> logger) : ISettingsStore
{
    public AppSettings Load()
    {
        var values = new Dictionary<string, string>();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM settings;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return Parse(values, key => LogInvalid(logger, key));
    }

    public void Save(AppSettings settings)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in Values(settings))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) "
                + "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Настройки из пар «ключ → значение» — из базы или из резервной копии. Нет ключа или значение не
    /// читается — значение по умолчанию; о негодном сообщает <paramref name="invalid"/>.
    /// </summary>
    internal static AppSettings Parse(IReadOnlyDictionary<string, string> values, Action<string> invalid)
    {
        var d = AppSettings.Default;
        var p = d.Protection;
        var protection = new ProtectionSettings(
            Bool("protection.boot", p.BootProtection),
            Bool("protection.strict", p.StrictMode),
            Enum("protection.dns", p.Dns),
            values.TryGetValue("protection.dns_custom", out var custom) ? DnsAddress.Normalize(custom) : p.CustomDns,
            Bool("protection.block_outside_dns", p.BlockOutsideDns),
            Bool("protection.fake_dns", p.FakeDns),
            Bool("protection.check_leaks", p.CheckLeaksAfterConnect),
            Bool("protection.block_ipv6", p.BlockIpv6),
            Enum("protection.addresses", p.Addresses),
            Bool("protection.allow_lan", p.AllowLan),
            Bool("protection.lan_discovery", p.LanDiscovery),
            Bool("protection.allow_other_vpn", p.AllowOtherVpn));
        var c = d.Connection;
        var core = new ConnectionSettings(
            Enum("connection.core_log", c.CoreLog),
            Enum("connection.tun_engine", c.Stack),
            Int("connection.mtu", ConnectionSettings.MinMtu, ConnectionSettings.MaxMtu) ?? c.Mtu,
            Bool("connection.strict_routes", c.StrictRoutes),
            Bool("connection.sniffing", c.Sniffing),
            Enum("connection.fingerprint", c.Fingerprint),
            Bool("connection.mux", c.Mux),
            Bool("connection.tls_fragment", c.TlsFragment),
            Int("connection.timeout", 1, 120) ?? c.ConnectTimeoutSeconds,
            Enum("connection.reconnect", c.Reconnect),
            Int("connection.proxy_port", 1024, 65535) ?? c.ProxyPort,
            Bool("connection.proxy_allow_lan", c.ProxyAllowLan),
            Bool("connection.system_proxy", c.SystemProxy),
            Bool("connection.vpn_system_proxy", c.VpnSystemProxy));
        var routing = new RoutingSettings(
            Enum("routing.mode", d.Routing.Mode),
            Json<BypassApp>("routing.apps").Where(a => !string.IsNullOrWhiteSpace(a.Path)).ToList(),
            [.. Json<DomainRule>("routing.rules").Select(r => SitePattern.Normalize(r.Pattern) is { } pattern ? r with { Pattern = pattern } : null)
                .OfType<DomainRule>()]);
        var proxyDefaults = d.ExternalProxy;
        var externalProxy = new ExternalProxySettings(
            Enum("external_proxy.protocol", proxyDefaults.Protocol),
            values.GetValueOrDefault("external_proxy.host", proxyDefaults.Host),
            Int("external_proxy.port", 1, 65535) ?? (values.ContainsKey("external_proxy.port") ? 0 : proxyDefaults.Port),
            Enum("external_proxy.authentication", values.ContainsKey("external_proxy.authentication") ? ProxyAuthentication.UserPassword : proxyDefaults.Authentication),
            values.GetValueOrDefault("external_proxy.credentials_key"),
            values.TryGetValue("external_proxy.revision", out var revisionText)
                && long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ? revision : 0);
        // Поврежденный протокол/способ авторизации не становится другим действующим выходом по умолчанию.
        var invalidProxyChoice = values.TryGetValue("external_proxy.protocol", out var proxyProtocol)
            && (!System.Enum.TryParse<ExternalProxyProtocol>(proxyProtocol, out var parsedProtocol) || !System.Enum.IsDefined(parsedProtocol))
            || values.TryGetValue("external_proxy.authentication", out var proxyAuthentication)
            && (!System.Enum.TryParse<ProxyAuthentication>(proxyAuthentication, out var parsedAuthentication) || !System.Enum.IsDefined(parsedAuthentication));
        externalProxy = !invalidProxyChoice && externalProxy.Normalize() is { } normalizedProxy ? normalizedProxy
            : externalProxy with { Host = string.Empty, CredentialsKey = ExternalProxySettings.IsCredentialsKey(externalProxy.CredentialsKey) ? externalProxy.CredentialsKey : null };
        return new AppSettings(
            Enum("mode", d.Mode),
            Bool("kill_switch", d.KillSwitch),
            Bool("start_with_system", d.StartWithSystem),
            Bool("connect_on_launch", d.ConnectOnLaunch),
            Bool("minimize_to_tray", d.MinimizeToTray),
            Enum("notifications", d.Notifications),
            values.TryGetValue("language", out var language) && language is "ru" or "en" ? language : d.Language,
            Enum("subscription_refresh", d.SubscriptionRefresh),
            Bool("update_only_via_vpn", d.UpdateOnlyViaVpn),
            Bool("notify_selected_server_change", d.NotifySelectedServerChange))
        {
            Protection = protection,
            Connection = core,
            ExternalProxy = externalProxy,
            Routing = routing,
            UpdateChannel = Enum("update_channel", d.UpdateChannel),
            CheckUpdates = Bool("check_updates", d.CheckUpdates),
            OnboardingDone = Bool("onboarding_done", d.OnboardingDone),
            LabExpert = Bool("lab_expert", d.LabExpert),
        };

        bool Bool(string key, bool fallback) =>
            !values.TryGetValue(key, out var text) ? fallback
            : bool.TryParse(text, out var value) ? value
            : Invalid(key, fallback);

        // Число в пределах; нет ключа, пусто или вне пределов — значение по умолчанию.
        int? Int(string key, int min, int max) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
                ? value
                : null;

        // Список JSON-ом; не читается — пустой, а запуск не падает.
        IReadOnlyList<T> Json<T>(string key)
        {
            if (!values.TryGetValue(key, out var text) || text.Length == 0)
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<T>>(text, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return Invalid<IReadOnlyList<T>>(key, []);
            }
        }

        T Enum<T>(string key, T fallback)
            where T : struct, Enum =>
            !values.TryGetValue(key, out var text) ? fallback
            : System.Enum.TryParse<T>(text, out var value) && System.Enum.IsDefined(value) ? value
            : Invalid(key, fallback);

        T Invalid<T>(string key, T fallback)
        {
            invalid(key);
            return fallback;
        }
    }

    /// <summary>Настройки парами «ключ → значение» — для базы и резервной копии.</summary>
    internal static Dictionary<string, string> Values(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Dictionary<string, string>
        {
            ["mode"] = settings.Mode.ToString(),
            ["kill_switch"] = settings.KillSwitch.ToString(CultureInfo.InvariantCulture),
            ["start_with_system"] = settings.StartWithSystem.ToString(CultureInfo.InvariantCulture),
            ["connect_on_launch"] = settings.ConnectOnLaunch.ToString(CultureInfo.InvariantCulture),
            ["minimize_to_tray"] = settings.MinimizeToTray.ToString(CultureInfo.InvariantCulture),
            ["notifications"] = settings.Notifications.ToString(),
            ["language"] = settings.Language,
            ["subscription_refresh"] = settings.SubscriptionRefresh.ToString(),
            ["update_only_via_vpn"] = settings.UpdateOnlyViaVpn.ToString(CultureInfo.InvariantCulture),
            ["notify_selected_server_change"] = settings.NotifySelectedServerChange.ToString(CultureInfo.InvariantCulture),
            ["protection.boot"] = Text(settings.Protection.BootProtection),
            ["protection.strict"] = Text(settings.Protection.StrictMode),
            ["protection.dns"] = settings.Protection.Dns.ToString(),
            ["protection.dns_custom"] = settings.Protection.CustomDns ?? string.Empty,
            ["protection.block_outside_dns"] = Text(settings.Protection.BlockOutsideDns),
            ["protection.fake_dns"] = Text(settings.Protection.FakeDns),
            ["protection.check_leaks"] = Text(settings.Protection.CheckLeaksAfterConnect),
            ["protection.block_ipv6"] = Text(settings.Protection.BlockIpv6),
            ["protection.addresses"] = settings.Protection.Addresses.ToString(),
            ["protection.allow_lan"] = Text(settings.Protection.AllowLan),
            ["protection.lan_discovery"] = Text(settings.Protection.LanDiscovery),
            ["protection.allow_other_vpn"] = Text(settings.Protection.AllowOtherVpn),
            ["connection.core_log"] = settings.Connection.CoreLog.ToString(),
            ["connection.tun_engine"] = settings.Connection.Stack.ToString(),
            ["connection.mtu"] = settings.Connection.Mtu?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["connection.strict_routes"] = Text(settings.Connection.StrictRoutes),
            ["connection.sniffing"] = Text(settings.Connection.Sniffing),
            ["connection.fingerprint"] = settings.Connection.Fingerprint.ToString(),
            ["connection.mux"] = Text(settings.Connection.Mux),
            ["connection.tls_fragment"] = Text(settings.Connection.TlsFragment),
            ["connection.timeout"] = settings.Connection.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["connection.reconnect"] = settings.Connection.Reconnect.ToString(),
            ["connection.proxy_port"] = settings.Connection.ProxyPort.ToString(CultureInfo.InvariantCulture),
            ["connection.proxy_allow_lan"] = Text(settings.Connection.ProxyAllowLan),
            ["connection.system_proxy"] = Text(settings.Connection.SystemProxy),
            ["connection.vpn_system_proxy"] = Text(settings.Connection.VpnSystemProxy),
            ["external_proxy.protocol"] = settings.ExternalProxy.Protocol.ToString(),
            ["external_proxy.host"] = settings.ExternalProxy.Host,
            ["external_proxy.port"] = settings.ExternalProxy.Port.ToString(CultureInfo.InvariantCulture),
            ["external_proxy.authentication"] = settings.ExternalProxy.Authentication.ToString(),
            ["external_proxy.credentials_key"] = settings.ExternalProxy.CredentialsKey ?? string.Empty,
            ["external_proxy.revision"] = settings.ExternalProxy.Revision.ToString(CultureInfo.InvariantCulture),
            ["routing.mode"] = settings.Routing.Mode.ToString(),
            ["routing.apps"] = JsonSerializer.Serialize(settings.Routing.Apps, JsonOptions),
            ["routing.rules"] = JsonSerializer.Serialize(settings.Routing.Rules, JsonOptions),
            ["update_channel"] = settings.UpdateChannel.ToString(),
            ["check_updates"] = Text(settings.CheckUpdates),
            ["onboarding_done"] = Text(settings.OnboardingDone),
            ["lab_expert"] = Text(settings.LabExpert),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static string Text(bool value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Настройка {Key} не читается — взято значение по умолчанию")]
    private static partial void LogInvalid(ILogger logger, string key);
}
