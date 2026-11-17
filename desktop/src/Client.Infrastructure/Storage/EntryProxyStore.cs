using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>Привязки VLESS-профилей к входному SOCKS5. В базе нет логина и пароля прокси.</summary>
public sealed class EntryProxyStore : IEntryProxyStore
{
    private const string CredentialsPrefix = "entry-proxy:";
    private const string SubscriptionPrefix = "entry-subscription:";
    private readonly SqliteDatabase _database;
    private readonly SqliteServerCatalog _servers;
    private readonly ISecretStore _secrets;
    private readonly Lock _gate = new();

    public EntryProxyStore(SqliteDatabase database, SqliteServerCatalog servers, ISecretStore secrets)
    {
        _database = database;
        _servers = servers;
        _secrets = secrets;
        Prune();
        servers.Changed += (_, _) =>
        {
            // Сбой очистки осиротевшего секрета не должен отменить уже выполненную правку сервера.
            try
            {
                Prune();
            }
            catch (Exception error) when (error is SqliteException or SecretStoreException or InvalidOperationException or IOException)
            {
            }
        };
    }

    public ImportOutcome Import(EntryChainBundle bundle, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Profiles.Count == 0 || !_secrets.IsAvailable)
        {
            throw new SecretStoreException("Хранилище секретов для входного прокси недоступно");
        }

        var proxy = bundle.EntryProxy;
        if (!ForwardProxyRules.IsValidEndpoint(proxy.Host, proxy.Port)
            || !ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5,
                new ServerCredentials(Username: proxy.Username, Password: proxy.Password)))
        {
            throw new ArgumentException("Параметры входного SOCKS5 некорректны", nameof(bundle));
        }

        if (!ValidSubscriptionUrl(bundle.SubscriptionUrl))
        {
            throw new ArgumentException("Адрес подписки входного узла некорректен", nameof(bundle));
        }

        var existingGroup = GroupForDevice(bundle.DeviceId);
        var adoptedGroup = false;
        Uri? previousUrl = null;
        if (existingGroup is not null)
        {
            previousUrl = SubscriptionUrlForDevice(bundle.DeviceId);
            if (_servers.SubscriptionUrl(existingGroup) is not { } catalogUrl
                || !SubscriptionAddress.Same(catalogUrl, previousUrl))
            {
                throw new SecretStoreException("Адрес подписки входного узла не совпадает с группой");
            }

            var updated = Update(existingGroup, previousUrl, bundle)
                ?? throw new InvalidOperationException("Подписка входного узла изменилась при повторном импорте");
            return new ImportOutcome(updated.Added, updated.Kept);
        }
        else if (((IServerStore)_servers).SubscriptionFor(bundle.SubscriptionUrl) is { } restoredGroup)
        {
            // Резервная копия каталога восстанавливает профили и URL с новыми id, но не секреты
            // входного SOCKS5. Повторный импорт пакета возвращает защиту этой группе.
            if (GroupHasProxy(restoredGroup))
            {
                throw new InvalidOperationException("Адрес подписки уже принадлежит другой цепочке");
            }

            existingGroup = restoredGroup;
            previousUrl = bundle.SubscriptionUrl;
            adoptedGroup = true;
        }

        foreach (var definition in bundle.Profiles.Select(profile => profile.Server))
        {
            if (_servers.IdFor(definition) is { } id
                && _servers.Servers.First(server => server.Id == id).SubscriptionId != existingGroup)
            {
                throw new InvalidOperationException("Профиль уже сохранен вне этой цепочки");
            }
        }

        var credentialsKey = proxy.Username.Length == 0 ? null : CredentialsPrefix + Guid.NewGuid().ToString("N");
        var subscriptionKey = SubscriptionPrefix + Guid.NewGuid().ToString("N");
        var before = _servers.Servers.Select(server => server.Id).ToHashSet(StringComparer.Ordinal);
        var previousCredentialsKey = (string?)null;
        var previousSubscriptionKey = (string?)null;
        var committed = false;
        string? groupId = existingGroup;
        var rotated = existingGroup is not null && previousUrl is not null
            && !SubscriptionAddress.Same(previousUrl, bundle.SubscriptionUrl);
        var oldTitle = existingGroup is null ? null : _servers.Subscriptions.First(group => group.Id == existingGroup).Name;
        try
        {
            if (credentialsKey is not null)
            {
                _secrets.Save(credentialsKey, JsonSerializer.Serialize(new ServerCredentials(Username: proxy.Username, Password: proxy.Password)));
            }

            _secrets.Save(subscriptionKey, bundle.SubscriptionUrl.OriginalString);
            if (rotated)
            {
                _servers.EditSubscription(existingGroup!, oldTitle!, bundle.SubscriptionUrl);
                if (_servers.SubscriptionUrl(existingGroup!) is not { } updated
                    || !SubscriptionAddress.Same(updated, bundle.SubscriptionUrl))
                {
                    throw new SecretStoreException("Адрес подписки входного узла не обновился");
                }
            }

            var definitions = bundle.Profiles.Select(profile => profile.Server).ToArray();
            var title = string.IsNullOrWhiteSpace(groupName) ? definitions[0].Name : groupName.Trim();
            var outcome = _servers.Add(definitions, title, bundle.SubscriptionUrl);
            groupId ??= ((IServerStore)_servers).SubscriptionFor(bundle.SubscriptionUrl)
                ?? throw new InvalidOperationException("Группа подписки входного узла не создана");
            var ids = definitions.Select(definition => _servers.IdFor(definition)
                ?? throw new InvalidOperationException("Сохраненный сервер пакета не найден")).Distinct(StringComparer.Ordinal).ToArray();

            lock (_gate)
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT credentials_key, subscription_key FROM entry_proxies WHERE device_id = $device";
                    select.Parameters.AddWithValue("$device", bundle.DeviceId);
                    using var reader = select.ExecuteReader();
                    if (reader.Read())
                    {
                        previousCredentialsKey = reader.IsDBNull(0) ? null : reader.GetString(0);
                        previousSubscriptionKey = reader.GetString(1);
                    }
                }

                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = "INSERT INTO entry_proxies (device_id, node_id, host, port, supports_udp, credentials_key, subscription_key, group_id) "
                        + "VALUES ($device, $node, $host, $port, $udp, $key, $subscription, $group) ON CONFLICT(device_id) DO UPDATE SET "
                        + "node_id = excluded.node_id, host = excluded.host, port = excluded.port, supports_udp = excluded.supports_udp, "
                        + "credentials_key = excluded.credentials_key, subscription_key = excluded.subscription_key, group_id = excluded.group_id";
                    upsert.Parameters.AddWithValue("$device", bundle.DeviceId);
                    upsert.Parameters.AddWithValue("$node", bundle.NodeId);
                    upsert.Parameters.AddWithValue("$host", proxy.Host);
                    upsert.Parameters.AddWithValue("$port", proxy.Port);
                    upsert.Parameters.AddWithValue("$udp", proxy.SupportsUdp ? 1 : 0);
                    upsert.Parameters.AddWithValue("$key", (object?)credentialsKey ?? DBNull.Value);
                    upsert.Parameters.AddWithValue("$subscription", subscriptionKey);
                    upsert.Parameters.AddWithValue("$group", groupId);
                    upsert.ExecuteNonQuery();
                }

                foreach (var id in ids)
                {
                    using var bind = connection.CreateCommand();
                    bind.Transaction = transaction;
                    bind.CommandText = "INSERT INTO entry_proxy_bindings (server_id, device_id) VALUES ($server, $device) "
                        + "ON CONFLICT(server_id) DO UPDATE SET device_id = excluded.device_id";
                    bind.Parameters.AddWithValue("$server", id);
                    bind.Parameters.AddWithValue("$device", bundle.DeviceId);
                    bind.ExecuteNonQuery();
                }

                transaction.Commit();
                committed = true;
            }

            Forget(previousCredentialsKey);
            Forget(previousSubscriptionKey);
            if (adoptedGroup)
            {
                // Восстановленный каталог мог содержать старые UUID. После возврата SOCKS-привязки
                // применить обычную замену подписки, чтобы удалить исчезнувшие профили группы.
                if (Update(groupId!, bundle.SubscriptionUrl, bundle) is null)
                {
                    throw new InvalidOperationException("Подписка входного узла изменилась при восстановлении");
                }
            }

            return outcome;
        }
        catch
        {
            if (!committed)
            {
                Forget(credentialsKey);
                Forget(subscriptionKey);
                foreach (var id in _servers.Servers.Select(server => server.Id).Where(id => !before.Contains(id)).ToArray())
                {
                    _servers.Remove(id);
                }

                if (existingGroup is null && groupId is not null)
                {
                    _servers.RemoveSubscription(groupId);
                }

                if (rotated)
                {
                    _servers.EditSubscription(existingGroup!, oldTitle!, previousUrl);
                }
            }

            throw;
        }
    }

    public SubscriptionUpdate? Update(string subscriptionId, Uri expectedUrl, EntryChainBundle bundle)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
        ArgumentNullException.ThrowIfNull(expectedUrl);
        ArgumentNullException.ThrowIfNull(bundle);
        if (!_secrets.IsAvailable)
        {
            throw new SecretStoreException("Хранилище секретов для входного прокси недоступно");
        }

        var incoming = bundle.EntryProxy;
        if (bundle.Profiles.Count == 0 || !ValidSubscriptionUrl(expectedUrl) || !ValidSubscriptionUrl(bundle.SubscriptionUrl)
            || !ForwardProxyRules.IsValidEndpoint(incoming.Host, incoming.Port)
            || !ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5,
                new ServerCredentials(Username: incoming.Username, Password: incoming.Password)))
        {
            throw new ArgumentException("Пакет входной цепочки некорректен", nameof(bundle));
        }

        string? oldCredentialsKey;
        string oldSubscriptionKey;
        lock (_gate)
        {
            using var connection = _database.Open();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT node_id, credentials_key, subscription_key FROM entry_proxies "
                + "WHERE group_id = $group AND device_id = $device";
            select.Parameters.AddWithValue("$group", subscriptionId);
            select.Parameters.AddWithValue("$device", bundle.DeviceId);
            using var reader = select.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != bundle.NodeId)
            {
                throw new SecretStoreException("Устройство входного узла не совпадает с сохраненной цепочкой");
            }

            oldCredentialsKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            oldSubscriptionKey = reader.GetString(2);
        }

        var savedUrl = ParseSubscriptionUrl(ReadRequiredSecret(oldSubscriptionKey, SubscriptionPrefix,
            "Адрес подписки входного узла недоступен"));
        if (!SubscriptionAddress.Same(savedUrl, expectedUrl))
        {
            return null;
        }

        var credentialsKey = incoming.Username.Length == 0 ? null : CredentialsPrefix + Guid.NewGuid().ToString("N");
        var subscriptionKey = SubscriptionPrefix + Guid.NewGuid().ToString("N");
        try
        {
            if (credentialsKey is not null)
            {
                _secrets.Save(credentialsKey, JsonSerializer.Serialize(new ServerCredentials(
                    Username: incoming.Username, Password: incoming.Password)));
            }

            _secrets.Save(subscriptionKey, bundle.SubscriptionUrl.OriginalString);
            var update = _servers.TryApplyEntryUpdate(subscriptionId, expectedUrl, bundle.SubscriptionUrl,
                [.. bundle.Profiles.Select(profile => profile.Server)], (connection, transaction) =>
                {
                    using var change = connection.CreateCommand();
                    change.Transaction = transaction;
                    change.CommandText = "UPDATE entry_proxies SET node_id = $node, host = $host, port = $port, "
                        + "supports_udp = $udp, credentials_key = $credentials, subscription_key = $subscription "
                        + "WHERE group_id = $group AND device_id = $device AND subscription_key = $old_subscription";
                    change.Parameters.AddWithValue("$node", bundle.NodeId);
                    change.Parameters.AddWithValue("$host", incoming.Host);
                    change.Parameters.AddWithValue("$port", incoming.Port);
                    change.Parameters.AddWithValue("$udp", incoming.SupportsUdp ? 1 : 0);
                    change.Parameters.AddWithValue("$credentials", (object?)credentialsKey ?? DBNull.Value);
                    change.Parameters.AddWithValue("$subscription", subscriptionKey);
                    change.Parameters.AddWithValue("$group", subscriptionId);
                    change.Parameters.AddWithValue("$device", bundle.DeviceId);
                    change.Parameters.AddWithValue("$old_subscription", oldSubscriptionKey);
                    if (change.ExecuteNonQuery() != 1)
                    {
                        throw new SecretStoreException("Привязка входного прокси изменилась во время обновления");
                    }

                    using var remove = connection.CreateCommand();
                    remove.Transaction = transaction;
                    remove.CommandText = "DELETE FROM entry_proxy_bindings WHERE device_id = $device "
                        + "AND server_id NOT IN (SELECT id FROM servers WHERE subscription_id = $group)";
                    remove.Parameters.AddWithValue("$device", bundle.DeviceId);
                    remove.Parameters.AddWithValue("$group", subscriptionId);
                    remove.ExecuteNonQuery();

                    using var bind = connection.CreateCommand();
                    bind.Transaction = transaction;
                    bind.CommandText = "INSERT INTO entry_proxy_bindings (server_id, device_id) "
                        + "SELECT id, $device FROM servers WHERE subscription_id = $group "
                        + "ON CONFLICT(server_id) DO UPDATE SET device_id = excluded.device_id";
                    bind.Parameters.AddWithValue("$device", bundle.DeviceId);
                    bind.Parameters.AddWithValue("$group", subscriptionId);
                    bind.ExecuteNonQuery();
                });
            if (update is null)
            {
                Forget(credentialsKey);
                Forget(subscriptionKey);
                return null;
            }

            Forget(oldCredentialsKey);
            Forget(oldSubscriptionKey);
            return update;
        }
        catch
        {
            // Callback Changed может бросить уже после commit каталога. В таком случае новые
            // ключи активны, и удалять их означало бы сломать подключение после успешной записи.
            bool active;
            try
            {
                active = IsActiveSubscriptionKey(subscriptionId, bundle.DeviceId, subscriptionKey);
            }
            catch (SqliteException)
            {
                active = true; // при неопределенности сохранить ключи безопаснее, чем удалить активные
            }

            if (active)
            {
                Forget(oldCredentialsKey);
                Forget(oldSubscriptionKey);
            }
            else
            {
                Forget(credentialsKey);
                Forget(subscriptionKey);
            }
            throw;
        }
    }

    private bool IsActiveSubscriptionKey(string groupId, string deviceId, string subscriptionKey)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM entry_proxies WHERE group_id = $group AND device_id = $device "
            + "AND subscription_key = $key LIMIT 1";
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$key", subscriptionKey);
        return command.ExecuteScalar() is not null;
    }

    public ServerDefinition? DefinitionFor(string serverId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT s.subscription_id, b.device_id FROM servers s "
                + "LEFT JOIN entry_proxy_bindings b ON b.server_id = s.id WHERE s.id = $server";
            command.Parameters.AddWithValue("$server", serverId);
            string? groupId;
            string? boundDevice;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                groupId = reader.IsDBNull(0) ? null : reader.GetString(0);
                boundDevice = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            using var proxy = connection.CreateCommand();
            proxy.CommandText = "SELECT device_id, host, port, credentials_key FROM entry_proxies "
                + "WHERE group_id = $group OR device_id = $device";
            proxy.Parameters.AddWithValue("$group", (object?)groupId ?? DBNull.Value);
            proxy.Parameters.AddWithValue("$device", (object?)boundDevice ?? DBNull.Value);
            using var proxies = proxy.ExecuteReader();
            if (!proxies.Read())
            {
                if (boundDevice is not null || groupId is not null && IsEntrySubscriptionGroup(groupId))
                {
                    throw new SecretStoreException("Привязка входного прокси повреждена");
                }

                return null;
            }

            var deviceId = proxies.GetString(0);
            var host = proxies.GetString(1);
            var port = proxies.GetInt32(2);
            var key = proxies.IsDBNull(3) ? null : proxies.GetString(3);
            if (proxies.Read() || boundDevice is not null && boundDevice != deviceId)
            {
                throw new SecretStoreException("Привязки входного прокси противоречат друг другу");
            }

            return ProxyDefinition(host, port, key);
        }
    }

    private bool IsEntrySubscriptionGroup(string groupId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT address FROM subscriptions WHERE id = $group";
        command.Parameters.AddWithValue("$group", groupId);
        if (command.ExecuteScalar() is string address
            && Uri.TryCreate(address, UriKind.Absolute, out var masked)
            && masked.AbsolutePath.StartsWith("/sub/entry/", StringComparison.Ordinal))
        {
            return true;
        }

        // Старые группы могли скрывать и сегмент /entry/; полный URL остаётся в OS secret store.
        return _servers.SubscriptionUrl(groupId)?.AbsolutePath.StartsWith("/sub/entry/", StringComparison.Ordinal) == true;
    }

    public EntryChainSubscription? SubscriptionFor(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new SecretStoreException("Адрес подписки входного узла некорректен");
        }

        if (!url.AbsolutePath.StartsWith("/sub/entry/", StringComparison.Ordinal))
        {
            return null;
        }

        if (!ValidSubscriptionUrl(url))
        {
            throw new SecretStoreException("Адрес подписки входного узла некорректен");
        }

        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT node_id, host, port, credentials_key, subscription_key FROM entry_proxies";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(4);
                var savedUrl = ParseSubscriptionUrl(ReadRequiredSecret(key, SubscriptionPrefix, "Адрес подписки входного узла недоступен"));
                if (!SubscriptionAddress.Same(savedUrl, url))
                {
                    continue;
                }

                var nodeId = reader.GetString(0);
                var host = reader.GetString(1);
                var port = reader.GetInt32(2);
                var credentialsKey = reader.IsDBNull(3) ? null : reader.GetString(3);
                return new EntryChainSubscription(nodeId, savedUrl, ProxyDefinition(host, port, credentialsKey));
            }

            // Адрес этого вида должен загружаться только через явно сохраненный входной SOCKS5.
            throw new SecretStoreException("Входной прокси для подписки не найден");
        }
    }

    private string? GroupForDevice(string deviceId)
    {
        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_id FROM entry_proxies WHERE device_id = $device";
            command.Parameters.AddWithValue("$device", deviceId);
            return command.ExecuteScalar() as string;
        }
    }

    private bool GroupHasProxy(string groupId)
    {
        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM entry_proxies WHERE group_id = $group LIMIT 1";
            command.Parameters.AddWithValue("$group", groupId);
            return command.ExecuteScalar() is not null;
        }
    }

    private Uri SubscriptionUrlForDevice(string deviceId)
    {
        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT subscription_key FROM entry_proxies WHERE device_id = $device";
            command.Parameters.AddWithValue("$device", deviceId);
            var key = command.ExecuteScalar() as string
                ?? throw new SecretStoreException("Адрес подписки входного узла недоступен");
            return ParseSubscriptionUrl(ReadRequiredSecret(key, SubscriptionPrefix, "Адрес подписки входного узла недоступен"));
        }
    }

    private static Uri ParseSubscriptionUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && ValidSubscriptionUrl(url)
            ? url : throw new SecretStoreException("Адрес подписки входного узла поврежден");

    private static bool ValidSubscriptionUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps
        && url.UserInfo.Length == 0 && url.Fragment.Length == 0 && url.Query.Length == 0
        && url.AbsolutePath.StartsWith("/sub/entry/", StringComparison.Ordinal)
        && url.AbsolutePath.Length > "/sub/entry/".Length;

    private string ReadRequiredSecret(string key, string prefix, string message)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || !_secrets.IsAvailable)
        {
            throw new SecretStoreException(message);
        }

        try
        {
            return _secrets.Read(key) ?? throw new SecretStoreException(message);
        }
        catch (Exception error) when (error is not SecretStoreException
            && (error is InvalidOperationException or IOException or UnauthorizedAccessException))
        {
            throw new SecretStoreException(message, error);
        }
    }

    private ServerDefinition ProxyDefinition(string host, int port, string? credentialsKey)
    {
        if (!ForwardProxyRules.IsValidEndpoint(host, port))
        {
            throw new SecretStoreException("Сохраненный входной прокси поврежден");
        }

        var credentials = new ServerCredentials();
        if (credentialsKey is not null)
        {
            var text = ReadRequiredSecret(credentialsKey, CredentialsPrefix, "Учетные данные входного прокси недоступны");
            try
            {
                credentials = JsonSerializer.Deserialize<ServerCredentials>(text)
                    ?? throw new SecretStoreException("Учетные данные входного прокси повреждены");
            }
            catch (JsonException error)
            {
                throw new SecretStoreException("Учетные данные входного прокси повреждены", error);
            }

            if (string.IsNullOrEmpty(credentials.Username) || string.IsNullOrEmpty(credentials.Password)
                || !ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, credentials))
            {
                throw new SecretStoreException("Учетные данные входного прокси повреждены");
            }
        }

        return new ServerDefinition("Entry SOCKS5", string.Empty, ProxyProtocol.Socks5, host, port,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, credentials);
    }

    private void Prune()
    {
        List<string> unusedKeys = [];
        lock (_gate)
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM entry_proxy_bindings WHERE server_id NOT IN (SELECT id FROM servers)");
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT credentials_key, subscription_key FROM entry_proxies "
                    + "WHERE group_id NOT IN (SELECT id FROM subscriptions)";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        unusedKeys.Add(reader.GetString(0));
                    }

                    unusedKeys.Add(reader.GetString(1));
                }
            }

            Execute(connection, transaction, "DELETE FROM entry_proxies WHERE group_id NOT IN (SELECT id FROM subscriptions)");
            transaction.Commit();
        }

        unusedKeys.ForEach(Forget);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Forget(string? key)
    {
        if (key is null || !key.StartsWith(CredentialsPrefix, StringComparison.Ordinal)
            && !key.StartsWith(SubscriptionPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _secrets.Delete(key);
        }
        catch (Exception error) when (error is SecretStoreException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // База уже не ссылается на этот ключ; повторного доступа к нему нет.
        }
    }
}
