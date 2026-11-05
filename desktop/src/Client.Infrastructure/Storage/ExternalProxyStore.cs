using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Infrastructure.Storage;

/// <summary>Одна внешняя цель подключения; ее секретная запись заменяется только вместе с сохраненными метаданными.</summary>
public sealed class ExternalProxyStore(SettingsService appSettings, ISecretStore secrets) : IExternalProxyStore
{
    private static readonly ConditionalWeakTable<SettingsService, Lock> Gates = new();

    // Резервная копия создает свой экземпляр хранилища: поколения и OS-записи общие для тех же настроек.
    private Lock Gate => Gates.GetValue(appSettings, static _ => new Lock());

    public bool CanStoreSecrets
    {
        get
        {
            try
            {
                return secrets.IsAvailable;
            }
            catch (Exception error) when (StoreFailure(error))
            {
                return false;
            }
        }
    }

    public string SecretStoreName => secrets.Name;

    /// <summary>Единая точка копии: метаданные A никогда не соединяются с секретом следующего сохранения B.</summary>
    internal (AppSettings Settings, ServerCredentials? Credentials) Capture(bool includeCredentials)
    {
        lock (Gate)
        {
            var snapshot = appSettings.Current;
            var credentials = includeCredentials && snapshot.ExternalProxy.Authentication == ProxyAuthentication.UserPassword
                ? Read(snapshot.ExternalProxy) : null;
            return (snapshot, credentials);
        }
    }

    public ServerCredentials? ReadCredentials()
    {
        lock (Gate)
        {
            var configured = appSettings.Current.ExternalProxy;
            if (configured.Authentication == ProxyAuthentication.None)
            {
                return new ServerCredentials();
            }

            return Read(configured);
        }
    }

    public ServerDefinition? Definition()
    {
        lock (Gate)
        {
            var configured = appSettings.Current.ExternalProxy.Normalize();
            if (configured is null)
            {
                return null;
            }

            var credentials = configured.Authentication == ProxyAuthentication.None ? new ServerCredentials() : Read(configured);
            if (credentials is null)
            {
                return null;
            }

            return new ServerDefinition("External proxy", string.Empty,
                configured.Protocol == ExternalProxyProtocol.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http,
                configured.Host, configured.Port, TransportOptions.Tcp,
                configured.Protocol == ExternalProxyProtocol.Https
                    ? new SecurityOptions(TransportSecurity.Tls, Sni: configured.Host)
                    : SecurityOptions.None,
                ProtocolOptions.Empty, credentials);
        }
    }

    public void Save(ExternalProxySettings settings, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize() ?? throw new InvalidOperationException("Параметры внешнего прокси некорректны");
        var credentials = new ServerCredentials(Username: username, Password: password);
        if (normalized.Authentication == ProxyAuthentication.UserPassword && !ValidCredentials(normalized.Protocol, credentials))
        {
            throw new InvalidOperationException("Учетные данные внешнего прокси некорректны");
        }

        lock (Gate)
        {
            Commit(null, normalized, normalized.Authentication == ProxyAuthentication.UserPassword ? credentials : null);
        }
    }

    /// <summary>Ссылка из переносимой копии не используется: ее секрет пересоздается в хранилище этой машины.</summary>
    internal void Restore(AppSettings restored, ServerCredentials? credentials)
    {
        lock (Gate)
        {
            var metadata = restored.ExternalProxy with { CredentialsKey = null };
            if (metadata.Authentication != ProxyAuthentication.UserPassword)
            {
                credentials = null;
            }
            else if (credentials is not null && !ValidCredentials(metadata.Protocol, credentials))
            {
                throw new InvalidOperationException("Учетные данные внешнего прокси в копии некорректны");
            }

            Commit(restored, metadata, credentials);
        }
    }

    private void Commit(AppSettings? restored, ExternalProxySettings metadata, ServerCredentials? credentials)
    {
        string? key = null;
        if (credentials is not null)
        {
            if (!CanStoreSecrets)
            {
                throw new InvalidOperationException("Хранилище учетных данных недоступно");
            }

            key = ExternalProxySettings.CredentialsPrefix + Guid.NewGuid().ToString("N");
            try
            {
                secrets.Save(key, JsonSerializer.Serialize(credentials));
            }
            catch (Exception error) when (StoreFailure(error))
            {
                Forget(key);
                throw new InvalidOperationException("Не удалось сохранить учетные данные внешнего прокси");
            }
        }

        AppSettings? next = null;
        string? previous = null;
        try
        {
            appSettings.Update(current =>
            {
                previous = current.ExternalProxy.CredentialsKey;
                var revision = metadata == ExternalProxySettings.Default && current.ExternalProxy == ExternalProxySettings.Default
                    ? 0 : checked(current.ExternalProxy.Revision + 1);
                // Обычное сохранение формы не перезаписывает язык и прочие поля, измененные за время запроса к ОС.
                next = (restored ?? current) with { ExternalProxy = metadata with { CredentialsKey = key, Revision = revision } };
                return next;
            });
        }
        catch
        {
            // Changed вызывается после записи. Сбой подписчика не должен удалять уже действующий секрет.
            Forget(next is not null && appSettings.Current.ExternalProxy == next.ExternalProxy ? previous : key);
            throw new InvalidOperationException("Не удалось сохранить настройки внешнего прокси");
        }

        Forget(previous);
    }

    private ServerCredentials? Read(ExternalProxySettings configured)
    {
        if (configured.Authentication != ProxyAuthentication.UserPassword || !ExternalProxySettings.IsCredentialsKey(configured.CredentialsKey))
        {
            return null;
        }

        try
        {
            if (!CanStoreSecrets || secrets.Read(configured.CredentialsKey!) is not { } text)
            {
                return null;
            }

            var credentials = JsonSerializer.Deserialize<ServerCredentials>(text);
            return credentials is not null && ValidCredentials(configured.Protocol, credentials) ? credentials : null;
        }
        catch (Exception error) when (StoreFailure(error))
        {
            return null;
        }
    }

    internal static bool ValidCredentials(ExternalProxyProtocol protocol, ServerCredentials credentials) =>
        !string.IsNullOrEmpty(credentials.Username) && !string.IsNullOrEmpty(credentials.Password)
        && ForwardProxyRules.AcceptsCredentials(protocol == ExternalProxyProtocol.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http, credentials);

    private void Forget(string? key)
    {
        if (!ExternalProxySettings.IsCredentialsKey(key))
        {
            return;
        }

        try
        {
            secrets.Delete(key!);
        }
        catch (Exception error) when (StoreFailure(error))
        {
            // Неудачная очистка не возвращает старую ссылку в настройки и не раскрывает запись в журнале.
        }
    }

    private static bool StoreFailure(Exception error) => error is InvalidOperationException or IOException or UnauthorizedAccessException
        or SecurityException or JsonException or NotSupportedException or FormatException or System.Security.Cryptography.CryptographicException
        or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException;
}
