using System.Net;
using System.Text;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Import;

/// <summary>
/// Загрузка подписки по http(s): не дольше <see cref="Timeout"/>, не больше <see cref="MaxBytes"/>
/// после распаковки. Адрес подписки — секрет: в ошибках только причина и код ответа. Название — из
/// заголовка Profile-Title. Сначала — как настроена система (ее прокси), а если до прокси не дойти — еще раз напрямую
/// (<paramref name="direct"/>): системный прокси часто оставляет другой VPN-клиент, и пока тот выключен, прокси на его
/// адресе нет — подписка не грузилась с «нет соединения с сервером подписки» (у пользователя на Windows 2026-10-29).
/// </summary>
/// <param name="http">Клиент с системным прокси.</param>
/// <param name="time">Часы для предела времени.</param>
/// <param name="direct">Клиент без прокси для второй попытки; null — без нее.</param>
public sealed class HttpSubscriptionDownloader(HttpClient http, TimeProvider time, HttpClient? direct = null) : ISubscriptionDownloader
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>Клиент для программы: сжатые ответы распаковываются, предел размера — после распаковки.</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

    /// <summary>То же без системного прокси — вторая попытка, когда прокси не отвечает.</summary>
    public static HttpClient CreateDirectClient() =>
        new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = false });

    public async Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Подписка загружается только по http(s).", nameof(url));
        }

        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            try
            {
                return await FetchAsync(http, url, linked.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (direct is not null && e.HttpRequestError is HttpRequestError.ProxyTunnelError or HttpRequestError.ConnectionError)
            {
                return await FetchAsync(direct, url, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SubscriptionDownloadException(DownloadFailure.Timeout, innerException: e);
        }
        catch (HttpRequestException e)
        {
            // Текст исключения может содержать адрес — наружу только причина.
            throw new SubscriptionDownloadException(e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => DownloadFailure.NameResolution,
                HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError => DownloadFailure.Connection,
                HttpRequestError.SecureConnectionError => DownloadFailure.Tls,
                _ => DownloadFailure.Network,
            }, innerException: e);
        }
    }

    private static async Task<SubscriptionContent> FetchAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PrivateGateway/0.1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SubscriptionDownloadException(DownloadFailure.Status, (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new SubscriptionDownloadException(DownloadFailure.TooLarge);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new SubscriptionDownloadException(DownloadFailure.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        var title = response.Headers.TryGetValues("Profile-Title", out var titles) ? titles.FirstOrDefault() : null;
        return new SubscriptionContent(
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), SubscriptionContent.TitleFrom(title));
    }
}
