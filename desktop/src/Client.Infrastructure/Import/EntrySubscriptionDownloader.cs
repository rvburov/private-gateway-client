using System.Net;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Import;

/// <summary>
/// Подписка пакета цепочки до запуска VPN загружается через его сохраненный SOCKS5. Для нее не бывает повторной
/// попытки напрямую или через системный прокси; обычные подписки сохраняют прежний порядок загрузки.
/// </summary>
public sealed class EntrySubscriptionDownloader(ISubscriptionDownloader ordinary, IEntryProxyStore entries, TimeProvider time)
    : ISubscriptionDownloader
{
    public async Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        EntryChainSubscription? subscription;
        try
        {
            subscription = entries.SubscriptionFor(url);
        }
        catch (SecretStoreException)
        {
            // Отсутствие секрета цепочки не разрешает обычную загрузку подписки в обход SOCKS.
            throw new SubscriptionDownloadException(DownloadFailure.Connection);
        }

        if (subscription is null)
        {
            return await ordinary.DownloadAsync(url, cancellationToken).ConfigureAwait(false);
        }

        var proxy = subscription.Proxy;
        if (url.Scheme != Uri.UriSchemeHttps || proxy.Port is < 1 or > 65535 || proxy.Protocol != ProxyProtocol.Socks5)
        {
            throw new SubscriptionDownloadException(DownloadFailure.Connection);
        }

        // WebProxy не содержит URI с логином/паролем: учетные данные живут только в хранилище ОС и в памяти запроса.
        var endpoint = new UriBuilder("socks5", proxy.Host, proxy.Port).Uri;
        var credentials = proxy.Credentials;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(endpoint)
            {
                Credentials = credentials.Username is { Length: > 0 } user && credentials.Password is { Length: > 0 } password
                    ? new NetworkCredential(user, password) : null,
            },
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var client = new HttpClient(handler, disposeHandler: false);
        try
        {
            return await new HttpSubscriptionDownloader(client, time).DownloadAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (SubscriptionDownloadException error)
        {
            // Исключение HttpClient может включать URL с токеном в InnerException; наружу только фиксированная причина.
            throw new SubscriptionDownloadException(error.Failure, error.StatusCode, retryAfter: error.RetryAfter);
        }
    }
}
