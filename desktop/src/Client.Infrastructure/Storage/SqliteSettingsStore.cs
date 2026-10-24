using System.Globalization;
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

        var d = AppSettings.Default;
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
            Bool("notify_selected_server_change", d.NotifySelectedServerChange));

        bool Bool(string key, bool fallback) =>
            !values.TryGetValue(key, out var text) ? fallback
            : bool.TryParse(text, out var value) ? value
            : Invalid(key, fallback);

        T Enum<T>(string key, T fallback)
            where T : struct, Enum =>
            !values.TryGetValue(key, out var text) ? fallback
            : System.Enum.TryParse<T>(text, out var value) && System.Enum.IsDefined(value) ? value
            : Invalid(key, fallback);
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = new Dictionary<string, string>
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
        };

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
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

    private T Invalid<T>(string key, T fallback)
    {
        LogInvalid(logger, key);
        return fallback;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Настройка {Key} не читается — взято значение по умолчанию")]
    private static partial void LogInvalid(ILogger logger, string key);
}
